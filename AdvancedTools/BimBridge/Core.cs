using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pilot.BimBridge.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AdvancedTools.BimBridge
{
    public static class BimBridgeClientStates
    {
        public const string Stopped = "STOPPED";
        public const string Connecting = "CONNECTING";
        public const string Registered = "REGISTERED";
        public const string Waiting = "WAITING";
        public const string Executing = "EXECUTING";
        public const string Uploading = "UPLOADING";
        public const string Reconnecting = "RECONNECTING";
        public const string Error = "ERROR";
        public const string Unsupported = "UNSUPPORTED";
    }

    public sealed class BimBridgeSnapshot
    {
        public static readonly BimBridgeSnapshot Initial = new BimBridgeSnapshot(
            BimBridgeClientStates.Stopped, null, Guid.Empty, null, null, 0, null, null, null,
            Array.Empty<string>(), Array.Empty<string>(), false, false);

        public BimBridgeSnapshot(
            string state,
            string workerInstanceId,
            Guid registrationId,
            Guid? currentTaskId,
            string currentOperation,
            int progressPercent,
            DateTime? lastHeartbeatUtc,
            string lastErrorCode,
            string lastError,
            IEnumerable<string> modelKeys,
            IEnumerable<string> capabilities,
            bool isRunning,
            bool isRegistered)
        {
            State = state;
            WorkerInstanceId = workerInstanceId;
            RegistrationId = registrationId;
            CurrentTaskId = currentTaskId;
            CurrentOperation = currentOperation;
            ProgressPercent = Math.Max(0, Math.Min(100, progressPercent));
            LastHeartbeatUtc = lastHeartbeatUtc;
            LastErrorCode = lastErrorCode;
            LastError = lastError;
            ModelKeys = (modelKeys ?? Array.Empty<string>()).ToArray();
            Capabilities = (capabilities ?? Array.Empty<string>()).ToArray();
            IsRunning = isRunning;
            IsRegistered = isRegistered;
        }

        public string State { get; }
        public string WorkerInstanceId { get; }
        public Guid RegistrationId { get; }
        public Guid? CurrentTaskId { get; }
        public string CurrentOperation { get; }
        public int ProgressPercent { get; }
        public DateTime? LastHeartbeatUtc { get; }
        public string LastErrorCode { get; }
        public string LastError { get; }
        public IReadOnlyList<string> ModelKeys { get; }
        public IReadOnlyList<string> Capabilities { get; }
        public bool IsRunning { get; }
        public bool IsRegistered { get; }

        public BimBridgeSnapshot With(
            string state = null,
            string workerInstanceId = null,
            Guid? registrationId = null,
            Guid? currentTaskId = null,
            bool clearCurrentTask = false,
            string currentOperation = null,
            int? progressPercent = null,
            DateTime? lastHeartbeatUtc = null,
            string lastErrorCode = null,
            string lastError = null,
            IEnumerable<string> modelKeys = null,
            IEnumerable<string> capabilities = null,
            bool? isRunning = null,
            bool? isRegistered = null,
            bool clearError = false)
        {
            return new BimBridgeSnapshot(
                state ?? State,
                workerInstanceId ?? WorkerInstanceId,
                registrationId ?? RegistrationId,
                clearCurrentTask ? (Guid?)null : currentTaskId ?? CurrentTaskId,
                clearCurrentTask ? null : currentOperation ?? CurrentOperation,
                progressPercent ?? ProgressPercent,
                lastHeartbeatUtc ?? LastHeartbeatUtc,
                clearError ? null : lastErrorCode ?? LastErrorCode,
                clearError ? null : lastError ?? LastError,
                modelKeys ?? ModelKeys,
                capabilities ?? Capabilities,
                isRunning ?? IsRunning,
                isRegistered ?? IsRegistered);
        }
    }

    public sealed class BimBridgeStateStore
    {
        private BimBridgeSnapshot _snapshot = BimBridgeSnapshot.Initial;
        public event EventHandler SnapshotChanged;

        public BimBridgeSnapshot Current => Volatile.Read(ref _snapshot);

        public void Update(Func<BimBridgeSnapshot, BimBridgeSnapshot> updater)
        {
            if (updater == null) throw new ArgumentNullException(nameof(updater));
            BimBridgeSnapshot before;
            BimBridgeSnapshot after;
            do
            {
                before = Current;
                after = updater(before) ?? throw new InvalidOperationException("Snapshot updater returned null.");
            }
            while (!ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, after, before), before));
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Collapses a burst of notifications into a single callback.  The callback
    /// is executed on a ThreadPool thread and is never allowed to overlap itself.
    /// </summary>
    public sealed class CoalescingSignal : IDisposable
    {
        private readonly Action _callback;
        private readonly int _delayMilliseconds;
        private readonly Timer _timer;
        private int _pending;
        private int _running;
        private int _disposed;

        public CoalescingSignal(TimeSpan delay, Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
            _callback = callback;
            _delayMilliseconds = Math.Max(1, (int)Math.Min(int.MaxValue, delay.TotalMilliseconds));
            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Request()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Interlocked.Exchange(ref _pending, 1) == 0)
                _timer.Change(_delayMilliseconds, Timeout.Infinite);
        }

        private void OnTimer(object state)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            try
            {
                Interlocked.Exchange(ref _pending, 0);
                try { _callback(); }
                catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _pending) != 0)
                    _timer.Change(_delayMilliseconds, Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Exchange(ref _pending, 0);
            _timer.Dispose();
        }
    }

    public sealed class BimBridgeSettings
    {
        public const int CurrentSettingsSchemaVersion = 5;
        public const string DefaultLoadedProjectName = "Графитек - АКВА";

        public int SettingsSchemaVersion { get; set; } = CurrentSettingsSchemaVersion;
        public bool AutoStart { get; set; }
        public string DurableWorkerId { get; set; } = Guid.NewGuid().ToString("N");
        public int MaxInFlightChunks { get; set; } = 3;
        public int MaxUploadChunkBytes { get; set; } = 4 * 1024 * 1024;
        public long MaxArtifactBytes { get; set; } = 8L * 1024 * 1024 * 1024;
        public long ArtifactDiskQuotaBytes { get; set; } = 16L * 1024 * 1024 * 1024;
        public bool SnapshotIndexEnabled { get; set; } = true;
        public long SnapshotIndexMaxBytes { get; set; } = 512L * 1024 * 1024;
        public int SnapshotIndexMaxEntries { get; set; } = 16;
        public bool MeshCacheEnabled { get; set; } = true;
        public long MeshCacheMaxBytes { get; set; } = 512L * 1024 * 1024;
        public int MeshCacheMaxEntries { get; set; } = 120000;
        public bool CoalescedNodeReadEnabled { get; set; } = true;
        public bool AsyncArtifactUploadEnabled { get; set; } = true;
        public int MaxPreparedArtifacts { get; set; } = 2;
        public long MaxPreparedArtifactBytes { get; set; } = 16L * 1024 * 1024 * 1024;
        public int ArtifactRetentionHours { get; set; } = 24;
        public long MaxExportElements { get; set; } = 10000000;
        public long SelectionDiskQuotaBytes { get; set; } = 1024L * 1024 * 1024;
        public int CursorRetentionMinutes { get; set; } = 60;
        public int MaxExportRecordBytes { get; set; } = 16 * 1024 * 1024;
        public long MaxBulkSpatialNodes { get; set; } = 10000000;
        public long SpatialScratchQuotaBytes { get; set; } = 2L * 1024 * 1024 * 1024;
        public bool AutoRegister { get; set; } = true;
        public bool ReconnectEnabled { get; set; } = true;
        public int OperationTimeoutSeconds { get; set; } = 1200;
        public int SdkDispatcherQueueTimeoutSeconds { get; set; } = 10;
        public int SearchServiceInitializationTimeoutSeconds { get; set; } = 180;
        public int SdkCleanupTimeoutSeconds { get; set; } = 15;
        public int ResultTransferTimeoutSeconds { get; set; } = 30;
        public int ResultFinalizeTimeoutSeconds { get; set; } = 60;
        public int ResultTransferMaxAttempts { get; set; } = 3;
        public bool DeleteTemporaryResults { get; set; } = true;
        public bool DiagnosticOperationsEnabled { get; set; }
        public string LogLevel { get; set; } = "Info";
        public string ResultsDirectory { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilotBimBridge", "Results");
        public string LoadedProjectNames { get; set; } = DefaultLoadedProjectName;
        public FileOperationsSettings FileOperations { get; set; } = new FileOperationsSettings();
        public ResourceLimitSettings ResourceLimits { get; set; } = new ResourceLimitSettings();

        public static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilotBimBridge", "settings.json");

        public static BimBridgeSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    var initial = new BimBridgeSettings(); initial.Save(); return initial;
                }
                var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var sourceVersion = JObject.Parse(json).Value<int?>(nameof(SettingsSchemaVersion)) ?? 0;
                var settings = JsonConvert.DeserializeObject<BimBridgeSettings>(json)
                               ?? new BimBridgeSettings();
                settings.FileOperations = settings.FileOperations ?? new FileOperationsSettings();
                settings.ResourceLimits = settings.ResourceLimits ?? new ResourceLimitSettings();
                settings.Migrate(sourceVersion);
                settings.Normalize();
                settings.Save(); // persist the stable identity when migrating an older settings file
                return settings;
            }
            catch
            {
                return new BimBridgeSettings();
            }
        }

        public void Save()
        {
            SettingsSchemaVersion = CurrentSettingsSchemaVersion;
            Normalize();
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
        }

        public string[] GetLoadedProjectNames()
        {
            Normalize();
            return LoadedProjectNames
                .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        private void Normalize()
        {
            if (!Guid.TryParse(DurableWorkerId, out _)) DurableWorkerId = Guid.NewGuid().ToString("N");
            MaxInFlightChunks = Math.Max(1, Math.Min(4, MaxInFlightChunks));
            MaxUploadChunkBytes=Math.Max(1024,Math.Min(4*1024*1024,MaxUploadChunkBytes));
            MaxExportElements=Math.Max(1,MaxExportElements);
            MaxBulkSpatialNodes=Math.Max(1,MaxBulkSpatialNodes);
            MaxExportRecordBytes=Math.Max(1024,Math.Min(64*1024*1024,MaxExportRecordBytes));
            SelectionDiskQuotaBytes=Math.Max(1048576,SelectionDiskQuotaBytes);
            SpatialScratchQuotaBytes=Math.Max(1048576,SpatialScratchQuotaBytes);
            CursorRetentionMinutes=Math.Max(1,CursorRetentionMinutes);
            MaxArtifactBytes = Math.Max(1048576, MaxArtifactBytes);
            ArtifactDiskQuotaBytes = Math.Max(MaxArtifactBytes, ArtifactDiskQuotaBytes);
            SnapshotIndexMaxBytes = Math.Max(16L*1024*1024, Math.Min(2L*1024*1024*1024, SnapshotIndexMaxBytes));
            SnapshotIndexMaxEntries = Math.Max(1, Math.Min(128, SnapshotIndexMaxEntries));
            MeshCacheMaxBytes = Math.Max(16L*1024*1024, Math.Min(2L*1024*1024*1024, MeshCacheMaxBytes));
            MeshCacheMaxEntries = Math.Max(1, Math.Min(1000000, MeshCacheMaxEntries));
            MaxPreparedArtifacts = Math.Max(1, Math.Min(16, MaxPreparedArtifacts));
            MaxPreparedArtifactBytes = Math.Max(MaxArtifactBytes, Math.Min(ArtifactDiskQuotaBytes, MaxPreparedArtifactBytes));
            ArtifactRetentionHours = Math.Max(1, ArtifactRetentionHours);
            if (SdkDispatcherQueueTimeoutSeconds <= 0) SdkDispatcherQueueTimeoutSeconds = 10;
            if (SearchServiceInitializationTimeoutSeconds <= 0) SearchServiceInitializationTimeoutSeconds = 180;
            if (SdkCleanupTimeoutSeconds <= 0) SdkCleanupTimeoutSeconds = 15;
            DeleteTemporaryResults = true;
            if (string.IsNullOrWhiteSpace(LoadedProjectNames))
                LoadedProjectNames = DefaultLoadedProjectName;
            FileOperations = FileOperations ?? new FileOperationsSettings();
            FileOperations.DeleteTemporaryFiles = true;
            ResourceLimits = ResourceLimits ?? new ResourceLimitSettings();
            ResourceLimits.Normalize();
        }

        private void Migrate(int sourceVersion)
        {
            if (sourceVersion < 2 && ResourceLimits.MinimumAvailableMemoryGb == 8)
                ResourceLimits.MinimumAvailableMemoryGb = 4;
            if (sourceVersion < 3)
            {
                DeleteTemporaryResults = true;
                FileOperations.DeleteTemporaryFiles = true;
                ResourceLimits.MaxCachedModelSearchServices = 0;
                ResourceLimits.MaxCachedPartSearchServices = 0;
            }
            SettingsSchemaVersion = CurrentSettingsSchemaVersion;
        }
    }

    public sealed class ResourceLimitSettings
    {
        public int MinimumFreeDiskSpaceGb { get; set; } = 100;
        public int CacheCreationThresholdGb { get; set; } = 120;
        public int CacheResumeThresholdGb { get; set; } = 125;
        public int MinimumAvailableMemoryGb { get; set; } = 4;
        public int CriticalAvailableMemoryGb { get; set; } = 2;
        public bool AllowExistingSearchIndexesBelowMemoryLimit { get; set; } = true;
        // Retained only so older settings.json files remain readable. SDK services
        // are task-scoped and are never cached between assignments.
        public int MaxCachedModelSearchServices { get; set; }
        public int MaxCachedPartSearchServices { get; set; }
        public int MaxSearchResults { get; set; } = 25000;
        public int MaxPropertyElements { get; set; } = 10000;
        public int MaxSpatialTraversalNodes { get; set; } = 100000;
        public long MaxSpatialResultBytes { get; set; } = 67108864;

        public ResourceLimitSettings Clone()
        {
            return new ResourceLimitSettings
            {
                MinimumFreeDiskSpaceGb = MinimumFreeDiskSpaceGb,
                CacheCreationThresholdGb = CacheCreationThresholdGb,
                CacheResumeThresholdGb = CacheResumeThresholdGb,
                MinimumAvailableMemoryGb = MinimumAvailableMemoryGb,
                CriticalAvailableMemoryGb = CriticalAvailableMemoryGb,
                AllowExistingSearchIndexesBelowMemoryLimit = AllowExistingSearchIndexesBelowMemoryLimit,
                MaxCachedModelSearchServices = MaxCachedModelSearchServices,
                MaxCachedPartSearchServices = MaxCachedPartSearchServices,
                MaxSearchResults = MaxSearchResults,
                MaxPropertyElements = MaxPropertyElements,
                MaxSpatialTraversalNodes = MaxSpatialTraversalNodes,
                MaxSpatialResultBytes = MaxSpatialResultBytes
            };
        }

        public void CopyFrom(ResourceLimitSettings source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            MinimumFreeDiskSpaceGb = source.MinimumFreeDiskSpaceGb;
            CacheCreationThresholdGb = source.CacheCreationThresholdGb;
            CacheResumeThresholdGb = source.CacheResumeThresholdGb;
            MinimumAvailableMemoryGb = source.MinimumAvailableMemoryGb;
            CriticalAvailableMemoryGb = source.CriticalAvailableMemoryGb;
            AllowExistingSearchIndexesBelowMemoryLimit = source.AllowExistingSearchIndexesBelowMemoryLimit;
            MaxCachedModelSearchServices = source.MaxCachedModelSearchServices;
            MaxCachedPartSearchServices = source.MaxCachedPartSearchServices;
            MaxSearchResults = source.MaxSearchResults;
            MaxPropertyElements = source.MaxPropertyElements;
            MaxSpatialTraversalNodes = source.MaxSpatialTraversalNodes;
            MaxSpatialResultBytes = source.MaxSpatialResultBytes;
            Normalize();
        }

        public void ResetRecommended()
        {
            CopyFrom(new ResourceLimitSettings());
        }

        internal void Normalize()
        {
            MinimumFreeDiskSpaceGb = Math.Max(10, MinimumFreeDiskSpaceGb);
            CacheCreationThresholdGb = Math.Max(MinimumFreeDiskSpaceGb + 5, CacheCreationThresholdGb);
            CacheResumeThresholdGb = Math.Max(CacheCreationThresholdGb + 1, CacheResumeThresholdGb);
            CriticalAvailableMemoryGb = Math.Max(1, CriticalAvailableMemoryGb);
            MinimumAvailableMemoryGb = Math.Max(CriticalAvailableMemoryGb + 1, MinimumAvailableMemoryGb);
            MaxCachedModelSearchServices = 0;
            MaxCachedPartSearchServices = 0;
            MaxSearchResults = Math.Max(100, Math.Min(100000, MaxSearchResults));
            MaxPropertyElements = Math.Max(100, Math.Min(100000, MaxPropertyElements));
            MaxSpatialTraversalNodes = Math.Max(1, Math.Min(1000000, MaxSpatialTraversalNodes));
            MaxSpatialResultBytes = Math.Max(1048576L, Math.Min(268435456L, MaxSpatialResultBytes));
        }
    }

    public sealed class BimBridgeResourceSnapshot
    {
        public long FreeDiskBytes { get; set; }
        public long AvailableMemoryBytes { get; set; }
        public long ProcessPrivateBytes { get; set; }
        public bool IsDiskKnown { get; set; }
        public bool IsMemoryKnown { get; set; }
    }

    public interface IBimBridgeResourceMonitor
    {
        BimBridgeResourceSnapshot Capture();
    }

    public enum SearchResourceRequestKind
    {
        ExistingIndex,
        NewIndex
    }

    public sealed class ResourcePolicyDecision
    {
        public ResourcePolicyDecision(bool isAllowed, bool existingIndexesOnly, string reason)
        {
            IsAllowed = isAllowed;
            ExistingIndexesOnly = existingIndexesOnly;
            Reason = reason ?? string.Empty;
        }

        public bool IsAllowed { get; }
        public bool ExistingIndexesOnly { get; }
        public string Reason { get; }
    }

    public sealed class AdaptiveResourcePolicy
    {
        private readonly object _gate = new object();
        private bool _newIndexPressure;

        public ResourcePolicyDecision Evaluate(
            BimBridgeResourceSnapshot usage,
            ResourceLimitSettings limits,
            SearchResourceRequestKind requestKind)
        {
            usage = usage ?? new BimBridgeResourceSnapshot();
            limits = limits ?? new ResourceLimitSettings();
            limits.Normalize();

            var freeDiskGb = ToGigabytes(usage.FreeDiskBytes);
            var availableMemoryGb = ToGigabytes(usage.AvailableMemoryBytes);
            lock (_gate)
            {
                if (!usage.IsDiskKnown)
                {
                    _newIndexPressure = true;
                    return Block("Free disk space is unknown; the protected disk reserve cannot be guaranteed.");
                }
                if (freeDiskGb < limits.MinimumFreeDiskSpaceGb)
                {
                    _newIndexPressure = true;
                    return Block(string.Format(
                        "Free disk is {0:0.0} GB; at least {1} GB must remain free.",
                        freeDiskGb,
                        limits.MinimumFreeDiskSpaceGb));
                }
                if (usage.IsMemoryKnown && availableMemoryGb < limits.CriticalAvailableMemoryGb)
                {
                    _newIndexPressure = true;
                    return Block(string.Format(
                        "Available RAM is {0:0.0} GB; the critical reserve is {1} GB.",
                        availableMemoryGb,
                        limits.CriticalAvailableMemoryGb));
                }

                if (requestKind == SearchResourceRequestKind.ExistingIndex &&
                    limits.AllowExistingSearchIndexesBelowMemoryLimit)
                {
                    var existingOnly =
                        freeDiskGb < limits.CacheCreationThresholdGb ||
                        (usage.IsMemoryKnown && availableMemoryGb < limits.MinimumAvailableMemoryGb) ||
                        _newIndexPressure;
                    return new ResourcePolicyDecision(
                        true,
                        existingOnly,
                        existingOnly
                            ? "Only existing BIM search indexes are allowed by the current resource reserve."
                            : "Resources are sufficient for an existing BIM search index.");
                }

                var requiredDiskGb = _newIndexPressure
                    ? limits.CacheResumeThresholdGb
                    : limits.CacheCreationThresholdGb;
                var requiredMemoryGb = _newIndexPressure
                    ? limits.MinimumAvailableMemoryGb + 1
                    : limits.MinimumAvailableMemoryGb;
                var diskBlocked = freeDiskGb < requiredDiskGb;
                var memoryBlocked = usage.IsMemoryKnown && availableMemoryGb < requiredMemoryGb;
                if (diskBlocked || memoryBlocked)
                {
                    _newIndexPressure = true;
                    return Block(string.Format(
                        "A new BIM search index requires at least {0} GB free disk and {1} GB available RAM; current values are {2:0.0} GB and {3}.",
                        requiredDiskGb,
                        requiredMemoryGb,
                        freeDiskGb,
                        usage.IsMemoryKnown ? availableMemoryGb.ToString("0.0") + " GB" : "unknown"));
                }

                _newIndexPressure = false;
                return new ResourcePolicyDecision(true, false, "Resources are sufficient for a new BIM search index.");
            }
        }

        public bool IsNewIndexPressureActive
        {
            get { lock (_gate) return _newIndexPressure; }
        }

        private ResourcePolicyDecision Block(string reason) =>
            new ResourcePolicyDecision(false, true, reason);

        private static double ToGigabytes(long bytes) => bytes / (double)(1024L * 1024 * 1024);
    }

    public sealed class SearchIndexInventorySnapshot
    {
        private readonly HashSet<Guid> _indexIds;

        public SearchIndexInventorySnapshot(IEnumerable<Guid> indexIds, long totalLengthBytes, DateTime capturedAtUtc)
        {
            _indexIds = new HashSet<Guid>(indexIds ?? Enumerable.Empty<Guid>());
            TotalLengthBytes = Math.Max(0, totalLengthBytes);
            CapturedAtUtc = capturedAtUtc;
        }

        public long TotalLengthBytes { get; }
        public DateTime CapturedAtUtc { get; }
        public int Count => _indexIds.Count;
        public bool Contains(Guid id) => id != Guid.Empty && _indexIds.Contains(id);
        public bool ContainsAll(IEnumerable<Guid> ids)
        {
            var values = (ids ?? Enumerable.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToArray();
            return values.Length > 0 && values.All(_indexIds.Contains);
        }
    }

    public interface ISearchIndexInventory
    {
        SearchIndexInventorySnapshot Capture(bool forceRefresh = false);
    }

    public sealed class FileSystemSearchIndexInventory : ISearchIndexInventory
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
        private readonly object _gate = new object();
        private readonly string _root;
        private SearchIndexInventorySnapshot _cached;

        public FileSystemSearchIndexInventory(string root = null)
        {
            _root = string.IsNullOrWhiteSpace(root)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ASCON", "Pilot-BIM", "Pilot-BIM-Viewer", "Temp", "SearchIndexes")
                : root;
        }

        public SearchIndexInventorySnapshot Capture(bool forceRefresh = false)
        {
            lock (_gate)
            {
                if (!forceRefresh && _cached != null &&
                    DateTime.UtcNow - _cached.CapturedAtUtc < RefreshInterval)
                    return _cached;
            }

            var ids = new HashSet<Guid>();
            long total = 0;
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (var path in Directory.EnumerateFiles(_root, "*.indexbm2", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var file = new FileInfo(path);
                            if (file.Length <= 0) continue;
                            total += file.Length;
                            Guid id;
                            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file.Name), out id)) ids.Add(id);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            var snapshot = new SearchIndexInventorySnapshot(ids, total, DateTime.UtcNow);
            lock (_gate) _cached = snapshot;
            return snapshot;
        }
    }

    public sealed class SystemBimBridgeResourceMonitor : IBimBridgeResourceMonitor
    {
        public BimBridgeResourceSnapshot Capture()
        {
            var result = new BimBridgeResourceSnapshot();
            try
            {
                var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                var drive = new DriveInfo(root);
                result.FreeDiskBytes = drive.AvailableFreeSpace;
                result.IsDiskKnown = true;
            }
            catch { }

            try
            {
                var status = new MemoryStatusEx();
                if (GlobalMemoryStatusEx(status))
                {
                    result.AvailableMemoryBytes = checked((long)status.AvailablePhysical);
                    result.IsMemoryKnown = true;
                }
            }
            catch { }

            try { result.ProcessPrivateBytes = Process.GetCurrentProcess().PrivateMemorySize64; }
            catch { }
            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MemoryStatusEx
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
    }

    public static class BimBridgeResourceMonitor
    {
        private static readonly IBimBridgeResourceMonitor SystemMonitor = new SystemBimBridgeResourceMonitor();
        public static BimBridgeResourceSnapshot Capture() => SystemMonitor.Capture();
    }

    public sealed class FileOperationsSettings
    {
        public string TemporaryDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilotBimBridge", "Files");
        public long MaxSourceFileSizeBytes { get; set; } = 256L * 1024 * 1024;
        public long MaxExcelResultBytes { get; set; } = 256L * 1024 * 1024;
        public int MaxExcelSheets { get; set; } = 100;
        public int MaxExcelRowsPerSheet { get; set; } = 100000;
        public long MaxExcelCells { get; set; } = 1000000;
        public bool DeleteTemporaryFiles { get; set; } = true;
    }

    public interface IBimBridgeLog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception exception = null);
        string GetDiagnosticTail(int maxLines);
    }

    public sealed class FileBimBridgeLog : IBimBridgeLog
    {
        private const long MaximumTotalBytes = 256L * 1024 * 1024;
        private const int RetentionDays = 14;
        private readonly object _gate = new object();
        private readonly string _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilotBimBridge", "Logs");
        private DateTime _lastCleanupUtc = DateTime.MinValue;

        public FileBimBridgeLog()
        {
            try
            {
                Directory.CreateDirectory(_directory);
                lock (_gate) CleanupCore();
            }
            catch { }
        }

        public void Info(string message) => Write("INFO", message, null);
        public void Warn(string message) => Write("WARN", message, null);
        public void Error(string message, Exception exception = null) => Write("ERROR", message, exception);

        private void Write(string level, string message, Exception exception)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var safeMessage = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
                if (safeMessage.Length > 8192) safeMessage = safeMessage.Substring(0, 8192) + "…";
                var line = $"{DateTime.UtcNow:O} [{level}] {safeMessage}";
                if (exception != null)
                {
                    var current = exception;
                    var depth = 0;
                    while (current != null && depth < 4)
                    {
                        var separator = depth == 0 ? " | " : " -> ";
                        line += separator + current.GetType().Name + ": " +
                                (current.Message ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
                        current = current.InnerException;
                        depth++;
                    }
                }
                lock (_gate)
                {
                    File.AppendAllText(Path.Combine(_directory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log"), line + Environment.NewLine, Encoding.UTF8);
                    if (DateTime.UtcNow - _lastCleanupUtc >= TimeSpan.FromHours(1)) CleanupCore();
                }
            }
            catch
            {
                // Logging must never break the Pilot extension.
            }
        }

        public string GetDiagnosticTail(int maxLines)
        {
            try
            {
                var path = Path.Combine(_directory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");
                if (!File.Exists(path)) return string.Empty;
                var tail = new Queue<string>(Math.Max(1, maxLines));
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 81920))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (tail.Count >= Math.Max(1, maxLines)) tail.Dequeue();
                        tail.Enqueue(line);
                    }
                }
                return string.Join(Environment.NewLine, tail);
            }
            catch
            {
                return string.Empty;
            }
        }

        private void CleanupCore()
        {
            _lastCleanupUtc = DateTime.UtcNow;
            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(_directory).GetFiles("*.log")
                    .OrderBy(x => x.LastWriteTimeUtc)
                    .ToArray();
            }
            catch { return; }

            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in files.Where(x => x.LastWriteTimeUtc < cutoff))
            {
                try { file.Delete(); }
                catch { }
            }

            try
            {
                files = new DirectoryInfo(_directory).GetFiles("*.log")
                    .OrderBy(x => x.LastWriteTimeUtc)
                    .ToArray();
                var total = files.Sum(x => x.Length);
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log";
                foreach (var file in files)
                {
                    if (total <= MaximumTotalBytes) break;
                    if (string.Equals(file.Name, today, StringComparison.OrdinalIgnoreCase)) continue;
                    var length = file.Length;
                    try
                    {
                        file.Delete();
                        total -= length;
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    public sealed class BimBridgeException : Exception
    {
        public BimBridgeException(string code, string message, bool retryable = false, Exception inner = null)
            : base(message, inner)
        {
            Code = code;
            IsRetryable = retryable;
        }

        public string Code { get; }
        public bool IsRetryable { get; }

        public static void ThrowIfError(BimBridgeResponseDto response)
        {
            if (response?.Error != null)
                throw new BimBridgeException(response.Error.Code, response.Error.Message, response.Error.IsRetryable);
        }
    }

    public sealed class ReconnectDelayPolicy
    {
        private static readonly int[] Seconds = { 1, 2, 5, 10, 30 };
        private readonly Random _random;

        public ReconnectDelayPolicy(int? seed = null)
        {
            _random = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        public TimeSpan GetDelay(int attempt)
        {
            var index = Math.Max(0, Math.Min(Seconds.Length - 1, attempt));
            var jitter = 0.9 + _random.NextDouble() * 0.2;
            return TimeSpan.FromMilliseconds(Seconds[index] * 1000 * jitter);
        }
    }

    internal static class BimBridgeIdentity
    {
        private static readonly Guid InstanceGuid = Guid.NewGuid();

        public static string Create(int pilotUserId)
        {
            return $"{Environment.MachineName}:{Process.GetCurrentProcess().SessionId}:{pilotUserId}:{InstanceGuid:D}";
        }

        public static string AssemblyVersion =>
            typeof(BimBridgeIdentity).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        public static string BimSdkVersion
        {
            get
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(x => string.Equals(x.GetName().Name, "Ascon.Pilot.Bim.SDK", StringComparison.OrdinalIgnoreCase));
                return assembly?.GetName().Version?.ToString() ?? "unknown";
            }
        }
    }
}
