using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace AdvancedTools.BimBridge
{
    /// <summary>
    /// Last-resort ownership boundary for one manager assignment. Handlers should
    /// still dispose resources as soon as they stop using them; this scope makes
    /// every error, cancellation and lease-loss path deterministic as well.
    /// </summary>
    public sealed class OperationResourceScope : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Stack<IDisposable> _resources = new Stack<IDisposable>();
        private readonly IBimBridgeLog _log;
        private readonly CancellationTokenSource _cancellation;
        private bool _disposed;

        public OperationResourceScope(IBimBridgeLog log, CancellationTokenSource cancellation = null)
        {
            _log = log;
            _cancellation = cancellation;
        }

        public T Track<T>(T resource) where T : class, IDisposable
        {
            if (resource == null) return null;
            lock (_gate)
            {
                if (_disposed)
                {
                    resource.Dispose();
                    throw new ObjectDisposedException(nameof(OperationResourceScope));
                }
                _resources.Push(resource);
            }
            return resource;
        }

        public void Dispose()
        {
            IDisposable[] resources;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                resources = _resources.ToArray();
                _resources.Clear();
            }
            try { _cancellation?.Cancel(); }
            catch { }
            foreach (var resource in resources)
            {
                try { resource.Dispose(); }
                catch (Exception exception)
                {
                    _log?.Warn("Unable to release an operation resource: " + exception.Message);
                }
            }
        }
    }

    /// <summary>
    /// Owns all worker-created files for one Pilot process.  The exclusive lock
    /// prevents a newly started worker from deleting files that still belong to
    /// another live Pilot process.
    /// </summary>
    public sealed class WorkerTemporaryWorkspace : IDisposable
    {
        private readonly IBimBridgeLog _log;
        private readonly string[] _roots;
        private readonly FileStream[] _locks;
        private int _disposed;

        public WorkerTemporaryWorkspace(BimBridgeSettings settings, IBimBridgeLog log)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _log = log;
            var instanceName = Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
            var resultsBase = ResolveBaseDirectory(settings.ResultsDirectory);
            var filesBase = ResolveBaseDirectory(settings.FileOperations.TemporaryDirectory);
            ResultsDirectory = CreateInstanceDirectory(resultsBase, instanceName);
            FilesDirectory = string.Equals(resultsBase, filesBase, StringComparison.OrdinalIgnoreCase)
                ? ResultsDirectory
                : CreateInstanceDirectory(filesBase, instanceName);
            _roots = new[] { ResultsDirectory, FilesDirectory }
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _locks = _roots.Select(OpenLock).ToArray();
        }

        public string ResultsDirectory { get; }
        public string FilesDirectory { get; }

        public long CurrentFilesLength
        {
            get
            {
                long total = 0;
                foreach (var root in _roots)
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        {
                            if (string.Equals(Path.GetFileName(file), "worker.lock", StringComparison.OrdinalIgnoreCase)) continue;
                            try { total = checked(total + new FileInfo(file).Length); }
                            catch { }
                        }
                    }
                    catch { }
                }
                return total;
            }
        }

        private static string ResolveBaseDirectory(string configuredDirectory)
        {
            var baseDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilotBimBridge", "Temp")
                : configuredDirectory;
            return Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string CreateInstanceDirectory(string baseDirectory, string instanceName)
        {
            var instances = Path.Combine(baseDirectory, "instances");
            Directory.CreateDirectory(instances);
            CleanupStaleInstances(instances);
            var result = Path.Combine(instances, instanceName);
            Directory.CreateDirectory(result);
            return result;
        }

        private static FileStream OpenLock(string root)
        {
            return new FileStream(
                Path.Combine(root, "worker.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
        }

        private static void CleanupStaleInstances(string instancesDirectory)
        {
            string[] directories;
            try { directories = Directory.GetDirectories(instancesDirectory); }
            catch { return; }
            foreach (var directory in directories)
            {
                FileStream probe = null;
                try
                {
                    var lockPath = Path.Combine(directory, "worker.lock");
                    if (File.Exists(lockPath))
                        probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    probe?.Dispose();
                    probe = null;
                    TemporaryFileCleaner.TryDeleteDirectory(directory);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally { probe?.Dispose(); }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var handle in _locks.Reverse())
            {
                try { handle.Dispose(); }
                catch (Exception exception) { _log?.Warn("Unable to close worker temporary lock: " + exception.Message); }
            }
            foreach (var root in _roots)
                if (!TemporaryFileCleaner.TryDeleteDirectory(root))
                    _log?.Warn("Worker temporary directory was queued for cleanup.");
        }
    }

    internal static class TemporaryFileCleaner
    {
        private static readonly ConcurrentQueue<CleanupItem> Pending = new ConcurrentQueue<CleanupItem>();
        private static readonly Timer RetryTimer = new Timer(RetryPending, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        private static int _retrying;

        public static int PendingCount => Pending.Count;

        public static bool TryDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            if (TryDeleteCore(path, false)) return true;
            Pending.Enqueue(new CleanupItem(path, false));
            return false;
        }

        public static bool TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            if (TryDeleteCore(path, true)) return true;
            Pending.Enqueue(new CleanupItem(path, true));
            return false;
        }

        private static bool TryDeleteCore(string path, bool directory)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (directory)
                    {
                        if (!Directory.Exists(path)) return true;
                        Directory.Delete(path, true);
                    }
                    else
                    {
                        if (!File.Exists(path)) return true;
                        File.Delete(path);
                    }
                    return true;
                }
                catch
                {
                    if (attempt < 2) Thread.Sleep(attempt == 0 ? 100 : 500);
                }
            }
            return directory ? !Directory.Exists(path) : !File.Exists(path);
        }

        private static void RetryPending(object state)
        {
            if (Interlocked.CompareExchange(ref _retrying, 1, 0) != 0) return;
            try
            {
                var count = Pending.Count;
                for (var index = 0; index < count; index++)
                {
                    if (!Pending.TryDequeue(out var item)) break;
                    if (!TryDeleteCore(item.Path, item.IsDirectory)) Pending.Enqueue(item);
                }
            }
            finally { Interlocked.Exchange(ref _retrying, 0); }
        }

        private sealed class CleanupItem
        {
            public CleanupItem(string path, bool isDirectory)
            {
                Path = path;
                IsDirectory = isDirectory;
            }

            public string Path { get; }
            public bool IsDirectory { get; }
        }
    }
}
