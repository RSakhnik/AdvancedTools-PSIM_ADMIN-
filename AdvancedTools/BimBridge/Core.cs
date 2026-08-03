using Newtonsoft.Json;
using Pilot.BimBridge.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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

    public sealed class BimBridgeSettings
    {
        public bool AutoStart { get; set; }
        public bool AutoRegister { get; set; } = true;
        public bool ReconnectEnabled { get; set; } = true;
        public bool DeleteTemporaryResults { get; set; } = true;
        public bool DiagnosticOperationsEnabled { get; set; }
        public string LogLevel { get; set; } = "Info";
        public string ResultsDirectory { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilotBimBridge", "Results");
        public FileOperationsSettings FileOperations { get; set; } = new FileOperationsSettings();

        public static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilotBimBridge", "settings.json");

        public static BimBridgeSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new BimBridgeSettings();
                var settings = JsonConvert.DeserializeObject<BimBridgeSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8))
                               ?? new BimBridgeSettings();
                settings.FileOperations = settings.FileOperations ?? new FileOperationsSettings();
                return settings;
            }
            catch
            {
                return new BimBridgeSettings();
            }
        }

        public void Save()
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
        }
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
        private readonly object _gate = new object();
        private readonly string _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilotBimBridge", "Logs");

        public void Info(string message) => Write("INFO", message, null);
        public void Warn(string message) => Write("WARN", message, null);
        public void Error(string message, Exception exception = null) => Write("ERROR", message, exception);

        private void Write(string level, string message, Exception exception)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var safeMessage = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
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
                    File.AppendAllText(Path.Combine(_directory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log"), line + Environment.NewLine, Encoding.UTF8);
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
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - Math.Max(1, maxLines))));
            }
            catch
            {
                return string.Empty;
            }
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
