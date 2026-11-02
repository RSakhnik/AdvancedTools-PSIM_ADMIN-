using Newtonsoft.Json;
using Pilot.BimBridge.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
    public sealed class PreparedResult : IDisposable
    {
        public string Path { get; set; }
        public string FileName { get; set; }
        public string ResultKind { get; set; } = "json";
        public string ContentType { get; set; }
        public string ContentEncoding { get; set; }
        public long Length { get; set; }
        public string Sha256 { get; set; }
        public bool DeleteOnDispose { get; set; }

        public void Dispose()
        {
            if (string.IsNullOrWhiteSpace(Path)) return;
            TemporaryFileCleaner.TryDelete(Path);
        }
    }

    public sealed class ResultFileWriter
    {
        private const long CompressionThreshold = 1024 * 1024;
        private readonly BimBridgeSettings _settings;
        private readonly string _directory;
        internal string TemporaryDirectory => _directory;

        public ResultFileWriter(BimBridgeSettings settings, WorkerTemporaryWorkspace workspace = null)
        {
            _settings = settings;
            _directory = workspace?.ResultsDirectory ?? _settings.ResultsDirectory;
            Directory.CreateDirectory(_directory);
        }

        public async Task<PreparedResult> WriteAsync(Guid taskId, object result, CancellationToken cancellationToken)
        {
            var jsonPath = System.IO.Path.Combine(_directory, taskId.ToString("D") + ".json");
            var gzipPath = jsonPath + ".gz";
            TemporaryFileCleaner.TryDelete(jsonPath);
            TemporaryFileCleaner.TryDelete(gzipPath);
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var stream = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                    using (var text = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 81920))
                    using (var writer = new JsonTextWriter(text))
                    {
                        var serializer = JsonSerializer.CreateDefault();
                        serializer.Serialize(writer, result);
                        writer.Flush();
                    }
                }, cancellationToken).ConfigureAwait(false);

                var finalPath = jsonPath;
                var encoding = "identity";
                if (new FileInfo(jsonPath).Length > CompressionThreshold)
                {
                    await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using (var input = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
                        using (var output = new FileStream(gzipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                            input.CopyTo(gzip);
                    }, cancellationToken).ConfigureAwait(false);
                    TemporaryFileCleaner.TryDelete(jsonPath);
                    finalPath = gzipPath;
                    encoding = "gzip";
                }

                return new PreparedResult
                {
                    Path = finalPath,
                    FileName = taskId.ToString("D") + ".json",
                    ResultKind = "json",
                    ContentType = "application/json",
                    ContentEncoding = encoding,
                    Length = new FileInfo(finalPath).Length,
                    Sha256 = await ComputeSha256Async(finalPath, cancellationToken).ConfigureAwait(false),
                    DeleteOnDispose = true
                };
            }
            catch
            {
                TemporaryFileCleaner.TryDelete(jsonPath);
                TemporaryFileCleaner.TryDelete(gzipPath);
                throw;
            }
        }

        public async Task<PreparedResult> WriteStreamingJsonAsync(
            Guid taskId,
            Action<JsonTextWriter> write,
            CancellationToken cancellationToken)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            return await WriteStreamingJsonAsync(
                taskId,
                writer =>
                {
                    write(writer);
                    return Task.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);
        }

        public Task<PreparedResult> WriteStreamingJsonAsync(
            Guid taskId,
            Func<JsonTextWriter, Task> write,
            CancellationToken cancellationToken)
        {
            return WriteStreamingJsonAsync(taskId, write, cancellationToken, 0);
        }

        public async Task<PreparedResult> WriteStreamingJsonAsync(
            Guid taskId,
            Func<JsonTextWriter, Task> write,
            CancellationToken cancellationToken,
            long maximumJsonBytes)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            var jsonPath = System.IO.Path.Combine(_directory, taskId.ToString("D") + ".json");
            var gzipPath = jsonPath + ".gz";
            TemporaryFileCleaner.TryDelete(jsonPath);
            TemporaryFileCleaner.TryDelete(gzipPath);
            try
            {
                await Task.Run(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var stream = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                    using (var bounded = new SpatialLimitedStream(stream, maximumJsonBytes, cancellationToken))
                    using (var text = new StreamWriter(bounded, new System.Text.UTF8Encoding(false), 81920))
                    using (var writer = new JsonTextWriter(text))
                    {
                        await write(writer).ConfigureAwait(false);
                        writer.Flush();
                    }
                }, cancellationToken).ConfigureAwait(false);

                var finalPath = jsonPath;
                var encoding = "identity";
                if (new FileInfo(jsonPath).Length > CompressionThreshold)
                {
                    await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using (var input = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
                        using (var output = new FileStream(gzipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                            input.CopyTo(gzip);
                    }, cancellationToken).ConfigureAwait(false);
                    TemporaryFileCleaner.TryDelete(jsonPath);
                    finalPath = gzipPath;
                    encoding = "gzip";
                }

                return new PreparedResult
                {
                    Path = finalPath,
                    FileName = taskId.ToString("D") + ".json",
                    ResultKind = "json",
                    ContentType = "application/json",
                    ContentEncoding = encoding,
                    Length = new FileInfo(finalPath).Length,
                    Sha256 = await ComputeSha256Async(finalPath, cancellationToken).ConfigureAwait(false),
                    DeleteOnDispose = true
                };
            }
            catch
            {
                TemporaryFileCleaner.TryDelete(jsonPath);
                TemporaryFileCleaner.TryDelete(gzipPath);
                throw;
            }
        }

        public void CleanupOldFiles()
        {
            try
            {
                Directory.CreateDirectory(_directory);
                foreach (var file in Directory.EnumerateFiles(_directory, "*.json*"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }, cancellationToken);
        }
    }

    public static class TransportFailurePolicy
    {
        public static BimBridgeException Classify(Exception exception)
        {
            var bridge = exception as BimBridgeException;
            if (bridge != null && bridge.Code != BimBridgeErrorCodes.InternalError) return bridge;
            for (var cause = exception; cause != null; cause = cause.InnerException)
            {
                var name = cause.GetType().FullName ?? "";
                if (name.IndexOf("TransportException", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cause is System.Net.Http.HttpRequestException || cause is System.Net.WebException)
                    return new BimBridgeException("TRANSPORT_ERROR", exception.Message, true, exception);
            }
            if (exception is TimeoutException)
                return new BimBridgeException("SDK_TIMEOUT", exception.Message, true, exception);
            return bridge ?? new BimBridgeException(BimBridgeErrorCodes.InternalError, exception.Message, false, exception);
        }
    }

    public sealed class ResultUploader
    {
        private readonly IBimBridgeServerClient _client;
        private readonly BimBridgeSettings _settings;
        private readonly IBimBridgeLog _log;

        public ResultUploader(IBimBridgeServerClient client, BimBridgeSettings settings = null, IBimBridgeLog log = null)
        { _client = client; _settings = settings ?? new BimBridgeSettings(); _log = log; }

        private async Task<T> TransferAsync<T>(Func<Task<T>> call, string stage, int timeout, CancellationToken token)
        {
            var attempts = Math.Max(1, Math.Min(10, _settings.ResultTransferMaxAttempts));
            for (var attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                _log?.Info($"Result transfer start: {stage}; attempt={attempt}.");
                try
                {
                    var pending = call();
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        var delay = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, timeout)), deadline.Token);
                        if (await Task.WhenAny(pending, delay).ConfigureAwait(false) != pending)
                        {
                            _ = pending.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                            token.ThrowIfCancellationRequested();
                            throw new BimBridgeException("TRANSPORT_TIMEOUT", stage + " timed out.", true);
                        }
                        deadline.Cancel();
                        var result = await pending.ConfigureAwait(false);
                        var response = result as BimBridgeResponseDto;
                        if (response != null) BimBridgeException.ThrowIfError(response);
                        token.ThrowIfCancellationRequested();
                        _log?.Info($"Result transfer end: {stage}; attempt={attempt}; elapsedMs={watch.ElapsedMilliseconds}.");
                        return result;
                    }
                }
                catch (Exception error)
                {
                    token.ThrowIfCancellationRequested();
                    var failure = error is OperationCanceledException
                        ? new BimBridgeException("TRANSPORT_ERROR", stage + ": transport cancelled the call.", true, error)
                        : TransportFailurePolicy.Classify(error);
                    _log?.Warn($"Result transfer failure: {stage}; attempt={attempt}; elapsedMs={watch.ElapsedMilliseconds}; code={failure.Code}; type={error.GetType().FullName}.");
                    if (!failure.IsRetryable || attempt >= attempts ||
                        failure.Code == BimBridgeErrorCodes.WorkerNotRegistered ||
                        failure.Code == BimBridgeErrorCodes.WorkerOffline ||
                        failure.Code == BimBridgeErrorCodes.LeaseExpired || failure.Code == BimBridgeErrorCodes.LeaseLost ||
                        failure.Code == BimBridgeErrorCodes.AssignmentMismatch)
                        throw new BimBridgeException(failure.Code, stage + ": " + failure.Message, failure.IsRetryable, failure);
                    await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 1 : 3), token).ConfigureAwait(false);
                }
            }
        }

        public async Task UploadAndCompleteAsync(
            Guid registrationId,
            BimTaskAssignmentDto assignment,
            PreparedResult result,
            int chunkSize,
            Action<int, string> progress,
            CancellationToken cancellationToken)
        {
            if (chunkSize <= 0)
                throw new BimBridgeException("PROTOCOL_ERROR", "Server returned an invalid result chunk size.");
            var totalChunks = result.Length == 0 ? 0 : checked((int)Math.Ceiling(result.Length / (double)chunkSize));
            var description = new BeginResultUploadRequestDto
            {
                RegistrationId = registrationId,
                TaskId = assignment.TaskId,
                AssignmentId = assignment.AssignmentId,
                ContentType = result.ContentType,
                ContentEncoding = result.ContentEncoding,
                TotalLength = result.Length,
                TotalChunks = totalChunks,
                Sha256 = result.Sha256
                ,FileName = result.FileName
                ,ResultKind = result.ResultKind
            };
            var identity = $"task={assignment.TaskId}; assignment={assignment.AssignmentId}; bytes={result.Length}; chunks={totalChunks}";
            var begin = await TransferAsync(() => _client.BeginUploadAsync(description), "BeginUpload; " + identity,
                _settings.ResultTransferTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            BimBridgeException.ThrowIfError(begin);
            if (begin.ChunkSizeBytes != chunkSize)
                throw new BimBridgeException("PROTOCOL_ERROR",
                    $"Server chunk size changed from {chunkSize} to {begin.ChunkSizeBytes} bytes.");

            using (var stream = new FileStream(result.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
            {
                var reusableBuffer = new byte[chunkSize];
                for (var index = 0; index < totalChunks; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var expected = (int)Math.Min(chunkSize, result.Length - stream.Position);
                    var data = reusableBuffer;
                    var read = 0;
                    while (read < expected)
                    {
                        var count = await stream.ReadAsync(data, read, expected - read, cancellationToken).ConfigureAwait(false);
                        if (count == 0) throw new EndOfStreamException("Unexpected end of result file.");
                        read += count;
                    }
                    // A timed-out transport may still retain this array. Never reuse its payload.
                    var payload = new byte[expected];
                    Buffer.BlockCopy(data, 0, payload, 0, expected);
                    var chunkIndex = index;
                    await TransferAsync(() => _client.UploadChunkAsync(begin.UploadId, chunkIndex, payload),
                        $"UploadChunk; {identity}; upload={begin.UploadId}; chunk={index}; bytes={expected}",
                        _settings.ResultTransferTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                    progress?.Invoke((index + 1) * 100 / Math.Max(1, totalChunks), "Загрузка результата");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var complete = await TransferAsync(() => _client.CompleteTaskAsync(
                registrationId, assignment.TaskId, assignment.AssignmentId, begin.UploadId),
                $"CompleteTask; {identity}; upload={begin.UploadId}",
                _settings.ResultFinalizeTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            BimBridgeException.ThrowIfError(complete);
        }

    }

    public sealed class BimBridgeWorkerController : IDisposable
    {
        private readonly object _loopGate = new object();
        private readonly SemaphoreSlim _registrationGate = new SemaphoreSlim(1, 1);
        private readonly Func<IBimBridgeServerClient> _clientFactory;
        private readonly BimOperationDispatcher _dispatcher;
        private readonly ModelContextProvider _models;
        private readonly BimBridgeSettings _settings;
        private readonly IBimBridgeLog _log;
        private readonly ReconnectDelayPolicy _delayPolicy = new ReconnectDelayPolicy();
        private readonly ResultFileWriter _resultWriter;
        private readonly WorkerTemporaryWorkspace _workspace;
        private readonly string _workerInstanceId;
        private IBimBridgeServerClient _client;
        private RegisterWorkerResponseDto _registration;
        private CancellationTokenSource _runSource;
        private CancellationTokenSource _assignmentSource;
        private Task _loopTask;
        private Task _drainingOperation;
        private bool _registrationLost;

        internal static async Task<T> WithDeadlineAsync<T>(Task<T> task, int seconds)
        {
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)))).ConfigureAwait(false) != task)
            {
                _ = task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new BimBridgeException("TRANSPORT_TIMEOUT", "Pilot transport did not respond in time.", true);
            }
            return await task.ConfigureAwait(false);
        }
        private int _lastReportedProgress = -1;
        private bool _disposed;

        public BimBridgeWorkerController(
            Func<IBimBridgeServerClient> clientFactory,
            BimOperationDispatcher dispatcher,
            ModelContextProvider models,
            BimBridgeSettings settings,
            IBimBridgeLog log,
            int pilotUserId,
            ResultFileWriter resultWriter = null,
            WorkerTemporaryWorkspace workspace = null)
        {
            _clientFactory = clientFactory;
            _dispatcher = dispatcher;
            _models = models;
            _settings = settings;
            _log = log;
            _workspace = workspace;
            _resultWriter = resultWriter ?? new ResultFileWriter(settings, workspace);
            _resultWriter.CleanupOldFiles();
            _workerInstanceId = BimBridgeIdentity.Create(pilotUserId);
            State = new BimBridgeStateStore();
            _models.CatalogChanged += OnCatalogChanged;
            State.Update(s => s.With(
                workerInstanceId: _workerInstanceId,
                modelKeys: _models.GetModelKeys(),
                capabilities: _dispatcher.Capabilities));
        }

        public BimBridgeStateStore State { get; }
        public event EventHandler CatalogDisplayChanged;

        public string[] GetModelDisplayItems() => _models.GetDisplayItems();
        public string GetResourceStatus()
        {
            var temporaryBytes = _workspace?.CurrentFilesLength ?? 0;
            return _models.GetResourceStatus() + string.Format(
                " Временные файлы текущего экземпляра: {0:0.0} МБ; ожидают повторной очистки: {1}.",
                temporaryBytes / (1024d * 1024d),
                TemporaryFileCleaner.PendingCount);
        }
        public CatalogProgressSnapshot GetCatalogProgress() => _models.GetCatalogProgress();
        public void ApplyResourceSettings()
        {
            ThrowIfDisposed();
            _models.ApplyResourceLimits();
            CatalogDisplayChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task RegisterAsync()
        {
            ThrowIfDisposed();
            await _registrationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_registration != null && _registration.RegistrationId != Guid.Empty) return;
                State.Update(s => s.With(state: BimBridgeClientStates.Connecting, clearError: true));
                _client = _client ?? _clientFactory();
                var health = await WithDeadlineAsync(_client.GetHealthAsync(), 30).ConfigureAwait(false);
                BimBridgeException.ThrowIfError(health);
                if (!string.Equals(health.ProtocolVersion, BimBridgeProtocol.Version, StringComparison.Ordinal))
                    throw new BimBridgeException("UNSUPPORTED_PROTOCOL_VERSION",
                        $"Server protocol {health.ProtocolVersion} is incompatible with {BimBridgeProtocol.Version}.");

                var response = await WithDeadlineAsync(_client.RegisterWorkerAsync(new RegisterWorkerRequestDto
                {
                    ProtocolVersion = BimBridgeProtocol.Version,
                    WorkerInstanceId = _workerInstanceId,
                    MachineName = Environment.MachineName,
                    ExtensionVersion = BimBridgeIdentity.AssemblyVersion,
                    BimSdkVersion = BimBridgeIdentity.BimSdkVersion,
                    Capabilities = _dispatcher.Capabilities,
                    AvailableModelKeys = _models.GetModelKeys(),
                    MaxParallelTasks = 1
                }), 30).ConfigureAwait(false);
                BimBridgeException.ThrowIfError(response);
                if (response.ResultChunkSizeBytes <= 0)
                    throw new BimBridgeException("PROTOCOL_ERROR",
                        "BIM Bridge Manager does not provide ResultChunkSizeBytes. Update the server extension.");
                _registration = response;
                State.Update(s => s.With(
                    state: BimBridgeClientStates.Registered,
                    registrationId: response.RegistrationId,
                    isRegistered: true,
                    modelKeys: _models.GetModelKeys(),
                    capabilities: _dispatcher.Capabilities,
                    clearError: true));
                _log.Info("Worker registered: " + response.RegistrationId);
            }
            catch (BimBridgeException exception)
            {
                SetError(exception);
                throw;
            }
            catch (Exception exception)
            {
                var wrapped = new BimBridgeException(BimBridgeErrorCodes.InternalError, exception.Message, true, exception);
                SetError(wrapped);
                throw wrapped;
            }
            finally
            {
                _registrationGate.Release();
            }
        }

        public async Task StartAsync()
        {
            ThrowIfDisposed();
            if (_registration == null)
            {
                if (!_settings.AutoRegister)
                    throw new BimBridgeException(BimBridgeErrorCodes.WorkerNotRegistered, "Worker is not registered.");
                try { await RegisterAsync().ConfigureAwait(false); }
                catch (BimBridgeException failure) when (_settings.ReconnectEnabled &&
                    failure.Code != "UNSUPPORTED_PROTOCOL_VERSION")
                {
                    _registration = null;
                    _client = null;
                    _log.Warn("Initial connection failed; background reconnect will continue.");
                }
            }
            lock (_loopGate)
            {
                if (_loopTask != null && !_loopTask.IsCompleted) return;
                _runSource?.Dispose();
                _runSource = new CancellationTokenSource();
                _loopTask = Task.Run(() => RunLoopAsync(_runSource.Token));
            }
            State.Update(s => s.With(state: BimBridgeClientStates.Waiting, isRunning: true, clearError: true));
        }

        public Task StopAsync() => StopLoopAsync(true);

        public async Task UnregisterAsync()
        {
            await StopLoopAsync(false).ConfigureAwait(false);
            await UnregisterCoreAsync().ConfigureAwait(false);
        }

        public async Task RefreshAsync()
        {
            ThrowIfDisposed();
            await _models.RefreshAsync().ConfigureAwait(false);
            _client = _client ?? _clientFactory();
            var health = await WithDeadlineAsync(_client.GetHealthAsync(), 30).ConfigureAwait(false);
            BimBridgeException.ThrowIfError(health);
            if (_registration != null)
            {
                var workers = await WithDeadlineAsync(_client.GetWorkersAsync(), 30).ConfigureAwait(false);
                var worker = workers.FirstOrDefault(x => x.RegistrationId == _registration.RegistrationId);
                if (worker == null)
                {
                    _registration = null;
                    _client = null;
                    _registrationLost = true;
                    State.Update(s => s.With(
                        state: s.IsRunning ? BimBridgeClientStates.Reconnecting : BimBridgeClientStates.Stopped,
                        registrationId: Guid.Empty,
                        isRegistered: false));
                }
            }
            State.Update(s => s.With(modelKeys: _models.GetModelKeys(), capabilities: _dispatcher.Capabilities));
        }

        private void OnCatalogChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            State.Update(s => s.With(
                modelKeys: _models.GetModelKeys(),
                capabilities: _dispatcher.Capabilities));
            CatalogDisplayChanged?.Invoke(this, EventArgs.Empty);
        }

        public string GetDiagnostics()
        {
            var s = State.Current;
            var catalog = _models.GetCatalogReadiness();
            return
                $"State: {s.State}{Environment.NewLine}" +
                $"WorkerInstanceId: {s.WorkerInstanceId}{Environment.NewLine}" +
                $"RegistrationId: {s.RegistrationId:D}{Environment.NewLine}" +
                $"CurrentTaskId: {s.CurrentTaskId}{Environment.NewLine}" +
                $"Operation: {s.CurrentOperation}{Environment.NewLine}" +
                $"Progress: {s.ProgressPercent}%{Environment.NewLine}" +
                $"LastHeartbeatUtc: {s.LastHeartbeatUtc:O}{Environment.NewLine}" +
                $"LastError: {s.LastErrorCode} {s.LastError}{Environment.NewLine}" +
                $"Resources: {GetResourceStatus()}{Environment.NewLine}" +
                $"PartsCatalog: {catalog.State}; unresolved={catalog.UnresolvedPartCount}; warnings={catalog.Warnings.Length}{Environment.NewLine}" +
                $"Capabilities: {string.Join(", ", s.Capabilities)}{Environment.NewLine}" +
                $"Models: {string.Join(", ", s.ModelKeys)}{Environment.NewLine}{Environment.NewLine}" +
                _log.GetDiagnosticTail(100);
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            var reconnectAttempt = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_registrationLost)
                    {
                        _registrationLost = false;
                        _registration = null;
                        _client = null;
                        State.Update(s => s.With(registrationId: Guid.Empty, isRegistered: false));
                    }
                    if (_registration == null) await RegisterAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (_drainingOperation == null)
                    {
                        var sdkCleanup = _models.GetPendingSdkCleanup();
                        if (!sdkCleanup.IsCompleted) _drainingOperation = sdkCleanup;
                    }
                    if (_drainingOperation != null || _models.IsPilotUiBlocked)
                    {
                        if (_models.IsPilotUiBlocked || !_drainingOperation.IsCompleted)
                        {
                            State.Update(s => s.With(state: BimBridgeClientStates.Reconnecting,
                                isRunning: true, lastErrorCode: _models.IsPilotUiBlocked ? "SDK_UI_BUSY" : _models.SdkRecoveryReason, lastError: _models.SdkStatus));
                            var recovery = await WithDeadlineAsync(_client.HeartbeatAsync(new WorkerHeartbeatRequestDto
                            {
                                RegistrationId = _registration.RegistrationId,
                                State = BimWorkerStates.Degraded,
                                AvailableModelKeys = _models.GetModelKeys(),
                                Capabilities = _dispatcher.Capabilities,
                                StatusMessage = _models.SdkStatus,
                                ClientTimeUtc = DateTime.UtcNow
                            }), 30).ConfigureAwait(false);
                            BimBridgeException.ThrowIfError(recovery);
                            if (!recovery.RegistrationIsValid)
                                throw new BimBridgeException(BimBridgeErrorCodes.WorkerNotRegistered, "Registration expired.", true);
                            State.Update(s => s.With(lastHeartbeatUtc: DateTime.UtcNow));
                            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _registration.HeartbeatIntervalSeconds)), token).ConfigureAwait(false);
                            continue;
                        }
                        try { await _drainingOperation.ConfigureAwait(false); }
                        catch (Exception late) { _log.Warn("Late SDK operation ended: " + late.Message); }
                        _drainingOperation = null;
                        continue;
                    }
                    State.Update(s => s.With(
                        state: BimBridgeClientStates.Waiting,
                        isRunning: true,
                        clearCurrentTask: true,
                        progressPercent: 0,
                        clearError: true,
                        modelKeys: _models.GetModelKeys()));
                    var response = await WithDeadlineAsync(_client.WaitForTaskAsync(
                        _registration.RegistrationId, _registration.LongPollingTimeoutSeconds),
                        Math.Max(30, _registration.LongPollingTimeoutSeconds + 15)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    BimBridgeException.ThrowIfError(response);
                    reconnectAttempt = 0;
                    if (response.HasTask && response.Assignment != null)
                    {
                        if (_models.IsPilotUiBlocked || !_models.GetPendingSdkCleanup().IsCompleted)
                        {
                            await ReportFailureAsync(response.Assignment,
                                new BimBridgeException(_models.IsPilotUiBlocked ? "SDK_UI_BUSY" : _models.SdkRecoveryReason,
                                    _models.SdkStatus, true), false, _registration.RegistrationId).ConfigureAwait(false);
                            continue;
                        }
                        await ExecuteAssignmentAsync(response.Assignment, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (BimBridgeException exception) when (
                    string.Equals(exception.Code, BimBridgeErrorCodes.WorkerNotRegistered, StringComparison.OrdinalIgnoreCase))
                {
                    _registration = null;
                    _client = null;
                    State.Update(s => s.With(registrationId: Guid.Empty, isRegistered: false));
                }
                catch (Exception exception)
                {
                    var bridge = TransportFailurePolicy.Classify(exception);
                    _log.Error("Worker loop failure.", bridge);
                    if (bridge.Code == "UNSUPPORTED_PROTOCOL_VERSION" || !_settings.ReconnectEnabled)
                    {
                        SetError(bridge);
                        break;
                    }
                    _registration = null;
                    _client = null;
                    State.Update(s => s.With(registrationId: Guid.Empty, isRegistered: false));
                    State.Update(s => s.With(
                        state: BimBridgeClientStates.Reconnecting,
                        lastErrorCode: bridge.Code,
                        lastError: bridge.Message,
                        isRunning: true));
                    try
                    {
                        await Task.Delay(_delayPolicy.GetDelay(reconnectAttempt++), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
            State.Update(s => s.With(
                state: s.IsRegistered ? BimBridgeClientStates.Registered : BimBridgeClientStates.Stopped,
                isRunning: false,
                clearCurrentTask: true,
                progressPercent: 0));
        }

        private async Task ExecuteAssignmentAsync(BimTaskAssignmentDto assignment, CancellationToken runToken)
        {
            var registrationId = _registration.RegistrationId;
            var operationSource = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            var operation = Task.Run(() => ExecuteAssignmentCoreAsync(assignment, operationSource.Token, runToken));
            _ = operation.ContinueWith(t => operationSource.Dispose(), TaskScheduler.Default);
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, _settings.OperationTimeoutSeconds));
            while (!operation.IsCompleted)
            {
                await Task.WhenAny(operation, Task.Delay(500)).ConfigureAwait(false);
                CancellationTokenSource source;
                lock (_loopGate) source = _assignmentSource;
                if (!runToken.IsCancellationRequested && DateTime.UtcNow < deadline &&
                    (source == null || !source.IsCancellationRequested)) continue;
                try { operationSource.Cancel(); source?.Cancel(); } catch (ObjectDisposedException) { }
                if (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Math.Min(_settings.SdkCleanupTimeoutSeconds, _settings.OperationTimeoutSeconds))))).ConfigureAwait(false) == operation) break;
                _drainingOperation = operation;
                var failure = new BimBridgeException("SDK_RECOVERY_PENDING",
                    "SDK operation has not stopped. Worker is awaiting resource cleanup without accepting new tasks.", true);
                await ReportFailureAsync(assignment, failure, runToken.IsCancellationRequested, registrationId).ConfigureAwait(false);
                _registrationLost = true;
                return;
            }
            await operation.ConfigureAwait(false);
        }

        private async Task ExecuteAssignmentCoreAsync(BimTaskAssignmentDto assignment, CancellationToken runToken, CancellationToken userToken)
        {
            var registration = _registration;
            var start = await WithDeadlineAsync(_client.StartTaskAsync(
                registration.RegistrationId, assignment.TaskId, assignment.AssignmentId), 30).ConfigureAwait(false);
            BimBridgeException.ThrowIfError(start);
            BimBridgeException heartbeatFailure = null;
            using (var source = CancellationTokenSource.CreateLinkedTokenSource(runToken))
            {
                lock (_loopGate) _assignmentSource = source;
                try
                {
                State.Update(s => s.With(
                    state: BimBridgeClientStates.Executing,
                    currentTaskId: assignment.TaskId,
                    currentOperation: assignment.OperationCode,
                    progressPercent: 0));
                Interlocked.Exchange(ref _lastReportedProgress, 0);
                var heartbeatTask = HeartbeatLoopAsync(assignment, source, x => heartbeatFailure = x);
                BimBridgeException taskFailure = null;
                var cancellationAcknowledged = false;
                using (var resources = new OperationResourceScope(_log, source))
                {
                    try
                    {
                        var resultObject = await _dispatcher.ExecuteAsync(new TaskExecutionContext
                        {
                            Assignment = assignment,
                            CancellationToken = source.Token,
                            ReportProgress = ReportTaskProgress,
                            Resources = resources
                        }).ConfigureAwait(false);
                        source.Token.ThrowIfCancellationRequested();
                        using (var result = resultObject as PreparedResult ?? await _resultWriter.WriteAsync(assignment.TaskId, resultObject, source.Token)
                                   .ConfigureAwait(false))
                        {
                            await EnsureLeaseAsync(assignment, source.Token).ConfigureAwait(false);
                            State.Update(s => s.With(state: BimBridgeClientStates.Uploading, progressPercent: 0));
                            Interlocked.Exchange(ref _lastReportedProgress, 0);
                            var uploader = new ResultUploader(_client, _settings, _log);
                            await uploader.UploadAndCompleteAsync(
                                registration.RegistrationId,
                                assignment,
                                result,
                                registration.ResultChunkSizeBytes,
                                ReportTaskProgress,
                                source.Token).ConfigureAwait(false);
                        }
                        _log.Info($"Task completed: {assignment.TaskId}, operation={assignment.OperationCode}");
                    }
                    catch (OperationCanceledException)
                    {
                        taskFailure = heartbeatFailure ??
                                      new BimBridgeException(userToken.IsCancellationRequested ? BimBridgeErrorCodes.TaskCancelled : "OPERATION_INTERRUPTED",
                                          "Task execution was interrupted.", !userToken.IsCancellationRequested);
                        cancellationAcknowledged = taskFailure.Code == BimBridgeErrorCodes.TaskCancelled;
                    }
                    catch (Exception exception)
                    {
                        taskFailure = TransportFailurePolicy.Classify(exception);
                        if (taskFailure.Code == "TRANSPORT_ERROR" || taskFailure.Code == "TRANSPORT_TIMEOUT" ||
                            taskFailure.Code == BimBridgeErrorCodes.WorkerNotRegistered ||
                            taskFailure.Code == BimBridgeErrorCodes.WorkerOffline ||
                            taskFailure.Code == BimBridgeErrorCodes.LeaseExpired) _registrationLost = true;
                    }
                }
                var pendingCleanup = _models.GetPendingSdkCleanup();
                if (!pendingCleanup.IsCompleted &&
                    await Task.WhenAny(pendingCleanup, Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Math.Min(_settings.SdkCleanupTimeoutSeconds, _settings.OperationTimeoutSeconds))))).ConfigureAwait(false) != pendingCleanup)
                {
                    _drainingOperation = pendingCleanup;
                    State.Update(s => s.With(state: BimBridgeClientStates.Reconnecting,
                        lastErrorCode: _models.SdkRecoveryReason, lastError: _models.SdkStatus));
                }
                _models.GetActiveResourceCounts(out var activeStorages, out var activeSearchServices);
                if (activeStorages != 0 || activeSearchServices != 0)
                    _log.Warn(string.Format(
                        "Task resource scope closed, but background SDK resources are still active: storage={0}, search={1}.",
                        activeStorages,
                        activeSearchServices));
                else
                    _log.Info("Task resource scope closed: storage=0, search services=0.");
                if (taskFailure != null)
                    await ReportFailureAsync(assignment, taskFailure, cancellationAcknowledged, registration.RegistrationId).ConfigureAwait(false);
                try
                {
                    try { await heartbeatTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception exception) { _log.Warn("Heartbeat loop ended with: " + exception.Message); }
                    lock (_loopGate) if (ReferenceEquals(_assignmentSource, source)) _assignmentSource = null;
                    if (_drainingOperation == null && !runToken.IsCancellationRequested)
                        State.Update(s => s.With(
                            state: BimBridgeClientStates.Waiting,
                            clearCurrentTask: true,
                            progressPercent: 0));
                }
                finally { source.Cancel(); }
                }
                finally
                {
                    lock (_loopGate)
                        if (ReferenceEquals(_assignmentSource, source)) _assignmentSource = null;
                    source.Cancel();
                }
            }
        }

        private async Task HeartbeatLoopAsync(
            BimTaskAssignmentDto assignment,
            CancellationTokenSource source,
            Action<BimBridgeException> onFailure)
        {
            while (!source.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(1, _registration.HeartbeatIntervalSeconds)),
                        source.Token).ConfigureAwait(false);
                    var response = await SendHeartbeatAsync(assignment).ConfigureAwait(false);
                    BimBridgeException.ThrowIfError(response);
                    if (!response.RegistrationIsValid)
                        throw new BimBridgeException(BimBridgeErrorCodes.WorkerNotRegistered, "Worker registration is no longer valid.");
                    if (response.CancellationRequested)
                        throw new BimBridgeException(BimBridgeErrorCodes.TaskCancelled, "Server requested task cancellation.");
                }
                catch (OperationCanceledException) when (source.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    var failure = exception as BimBridgeException ??
                                  new BimBridgeException(BimBridgeErrorCodes.LeaseLost, exception.Message, true, exception);
                    onFailure(failure);
                    if (failure.Code != BimBridgeErrorCodes.TaskCancelled) _registrationLost = true;
                    source.Cancel();
                    return;
                }
            }
        }

        private async Task EnsureLeaseAsync(BimTaskAssignmentDto assignment, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var response = await SendHeartbeatAsync(assignment).ConfigureAwait(false);
            BimBridgeException.ThrowIfError(response);
            if (!response.RegistrationIsValid)
                throw new BimBridgeException(BimBridgeErrorCodes.WorkerNotRegistered, "Worker registration is no longer valid.");
            if (response.CancellationRequested)
                throw new BimBridgeException(BimBridgeErrorCodes.TaskCancelled, "Server requested task cancellation.");
        }

        private async Task<WorkerHeartbeatResponseDto> SendHeartbeatAsync(BimTaskAssignmentDto assignment)
        {
            var degraded = _models.IsPilotUiBlocked || !_models.GetPendingSdkCleanup().IsCompleted;
            var response = await WithDeadlineAsync(_client.HeartbeatAsync(new WorkerHeartbeatRequestDto
            {
                RegistrationId = _registration.RegistrationId,
                State = degraded ? BimWorkerStates.Degraded : BimWorkerStates.Busy,
                CurrentTaskId = assignment.TaskId,
                AssignmentId = assignment.AssignmentId,
                AvailableModelKeys = _models.GetModelKeys(),
                Capabilities = _dispatcher.Capabilities,
                StatusMessage = degraded ? _models.SdkStatus : $"{assignment.OperationCode}; progress={State.Current.ProgressPercent}%",
                ClientTimeUtc = DateTime.UtcNow
            }), 30).ConfigureAwait(false);
            State.Update(s => s.With(lastHeartbeatUtc: DateTime.UtcNow, modelKeys: _models.GetModelKeys()));
            return response;
        }

        private async Task ReportFailureAsync(BimTaskAssignmentDto assignment, BimBridgeException failure, bool cancelled, Guid registrationId)
        {
            _log.Error($"Task failed: {assignment.TaskId}, code={failure.Code}", failure);
            try
            {
                var response = await WithDeadlineAsync(_client.FailTaskAsync(new FailTaskRequestDto
                {
                    RegistrationId = registrationId,
                    TaskId = assignment.TaskId,
                    AssignmentId = assignment.AssignmentId,
                    CancellationAcknowledged = cancelled || failure.Code == BimBridgeErrorCodes.TaskCancelled,
                    Error = new BimTaskErrorDto
                    {
                        Code = failure.Code,
                        Message = failure.Message,
                        IsRetryable = failure.IsRetryable
                    },
                    DiagnosticDetails = failure.GetType().Name
                }), 30).ConfigureAwait(false);
                BimBridgeException.ThrowIfError(response);
            }
            catch (Exception reportException)
            {
                _log.Error("Unable to report task failure.", reportException);
            }
        }

        private async Task StopLoopAsync(bool unregister)
        {
            Task loop;
            lock (_loopGate)
            {
                _assignmentSource?.Cancel();
                _runSource?.Cancel();
                loop = _loopTask;
            }
            if (loop != null)
                await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            if (unregister) await UnregisterCoreAsync().ConfigureAwait(false);
            State.Update(s => s.With(
                state: BimBridgeClientStates.Stopped,
                isRunning: false,
                clearCurrentTask: true,
                progressPercent: 0,
                lastErrorCode: !_models.GetPendingSdkCleanup().IsCompleted ? "SDK_CLEANUP_PENDING" : null,
                lastError: !_models.GetPendingSdkCleanup().IsCompleted ? _models.SdkStatus : null));
        }

        private async Task UnregisterCoreAsync()
        {
            var registration = _registration;
            _registration = null;
            if (registration != null && _client != null)
            {
                try
                {
                    var unregister = _client.UnregisterWorkerAsync(registration.RegistrationId);
                    if (await Task.WhenAny(unregister, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != unregister)
                    {
                        _ = unregister.ContinueWith(t => { var observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        throw new TimeoutException("Unregister transport timeout.");
                    }
                    await unregister.ConfigureAwait(false);
                }
                catch (Exception exception) { _log.Warn("Unable to unregister worker: " + exception.Message); }
            }
            State.Update(s => s.With(registrationId: Guid.Empty, isRegistered: false));
        }

        private void SetError(BimBridgeException exception)
        {
            _log.Error(exception.Code + ": " + exception.Message, exception);
            State.Update(s => s.With(
                state: exception.Code == BimBridgeErrorCodes.UnsupportedPilotVersion
                    ? BimBridgeClientStates.Unsupported
                    : BimBridgeClientStates.Error,
                lastErrorCode: exception.Code,
                lastError: exception.Message,
                isRunning: false));
        }

        private void ReportTaskProgress(int percent, string message)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            if (Interlocked.Exchange(ref _lastReportedProgress, percent) == percent) return;
            State.Update(s => s.With(progressPercent: percent));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BimBridgeWorkerController));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _models.CatalogChanged -= OnCatalogChanged;
            ShutdownCompletion = Task.Run(async () =>
            {
                try
                {
                    await StopLoopAsync(true).ConfigureAwait(false);
                    if (_loopTask != null) await _loopTask.ConfigureAwait(false);
                    if (_drainingOperation != null) await _drainingOperation.ConfigureAwait(false);
                    await _models.GetPendingSdkCleanup().ConfigureAwait(false);
                }
                catch (Exception error) { _log.Error("Asynchronous worker shutdown failed.", error); }
            });
        }

        public Task ShutdownCompletion { get; private set; } = Task.CompletedTask;

        public string SdkStatus => _models.SdkStatus;
        public bool ShowBlockingWindow() => _models.ShowBlockingWindow();
    }
}
