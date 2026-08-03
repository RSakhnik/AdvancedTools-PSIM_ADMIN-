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
            if (!DeleteOnDispose || string.IsNullOrWhiteSpace(Path)) return;
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch { }
        }
    }

    public sealed class ResultFileWriter
    {
        private const long CompressionThreshold = 1024 * 1024;
        private readonly BimBridgeSettings _settings;

        public ResultFileWriter(BimBridgeSettings settings)
        {
            _settings = settings;
            Directory.CreateDirectory(_settings.ResultsDirectory);
        }

        public async Task<PreparedResult> WriteAsync(Guid taskId, object result, CancellationToken cancellationToken)
        {
            var jsonPath = System.IO.Path.Combine(_settings.ResultsDirectory, taskId.ToString("D") + ".json");
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = new FileStream(jsonPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
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
                var gzipPath = jsonPath + ".gz";
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var input = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
                    using (var output = new FileStream(gzipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                    using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                        input.CopyTo(gzip);
                    File.Delete(jsonPath);
                }, cancellationToken).ConfigureAwait(false);
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
                DeleteOnDispose = _settings.DeleteTemporaryResults
            };
        }

        public void CleanupOldFiles()
        {
            try
            {
                Directory.CreateDirectory(_settings.ResultsDirectory);
                foreach (var file in Directory.EnumerateFiles(_settings.ResultsDirectory, "*.json*"))
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

    public sealed class ResultUploader
    {
        private readonly IBimBridgeServerClient _client;

        public ResultUploader(IBimBridgeServerClient client) { _client = client; }

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
            var begin = await _client.BeginUploadAsync(new BeginResultUploadRequestDto
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
            }).ConfigureAwait(false);
            BimBridgeException.ThrowIfError(begin);
            if (begin.ChunkSizeBytes != chunkSize)
                throw new BimBridgeException("PROTOCOL_ERROR",
                    $"Server chunk size changed from {chunkSize} to {begin.ChunkSizeBytes} bytes.");

            using (var stream = new FileStream(result.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
            {
                for (var index = 0; index < totalChunks; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var expected = (int)Math.Min(chunkSize, result.Length - stream.Position);
                    var data = new byte[expected];
                    var read = 0;
                    while (read < expected)
                    {
                        var count = await stream.ReadAsync(data, read, expected - read, cancellationToken).ConfigureAwait(false);
                        if (count == 0) throw new EndOfStreamException("Unexpected end of result file.");
                        read += count;
                    }
                    await UploadChunkWithRetryAsync(begin.UploadId, index, data, cancellationToken).ConfigureAwait(false);
                    progress?.Invoke((index + 1) * 100 / Math.Max(1, totalChunks), "Загрузка результата");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var complete = await _client.CompleteTaskAsync(
                registrationId, assignment.TaskId, assignment.AssignmentId, begin.UploadId).ConfigureAwait(false);
            BimBridgeException.ThrowIfError(complete);
        }

        private async Task UploadChunkWithRetryAsync(Guid uploadId, int index, byte[] data, CancellationToken token)
        {
            BimBridgeException last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var response = await _client.UploadChunkAsync(uploadId, index, data).ConfigureAwait(false);
                    BimBridgeException.ThrowIfError(response);
                    return;
                }
                catch (BimBridgeException exception) when (exception.IsRetryable && attempt < 2)
                {
                    last = exception;
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token).ConfigureAwait(false);
                }
            }
            throw last ?? new BimBridgeException(BimBridgeErrorCodes.InternalError, "Unable to upload result chunk.");
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
        private readonly string _workerInstanceId;
        private IBimBridgeServerClient _client;
        private RegisterWorkerResponseDto _registration;
        private CancellationTokenSource _runSource;
        private CancellationTokenSource _assignmentSource;
        private Task _loopTask;
        private bool _disposed;

        public BimBridgeWorkerController(
            Func<IBimBridgeServerClient> clientFactory,
            BimOperationDispatcher dispatcher,
            ModelContextProvider models,
            BimBridgeSettings settings,
            IBimBridgeLog log,
            int pilotUserId)
        {
            _clientFactory = clientFactory;
            _dispatcher = dispatcher;
            _models = models;
            _settings = settings;
            _log = log;
            _resultWriter = new ResultFileWriter(settings);
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

        public string[] GetModelDisplayItems() => _models.GetDisplayItems();

        public async Task RegisterAsync()
        {
            ThrowIfDisposed();
            await _registrationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_registration != null && _registration.RegistrationId != Guid.Empty) return;
                State.Update(s => s.With(state: BimBridgeClientStates.Connecting, clearError: true));
                _client = _client ?? _clientFactory();
                var health = await _client.GetHealthAsync().ConfigureAwait(false);
                BimBridgeException.ThrowIfError(health);
                if (!string.Equals(health.ProtocolVersion, BimBridgeProtocol.Version, StringComparison.Ordinal))
                    throw new BimBridgeException("UNSUPPORTED_PROTOCOL_VERSION",
                        $"Server protocol {health.ProtocolVersion} is incompatible with {BimBridgeProtocol.Version}.");

                var response = await _client.RegisterWorkerAsync(new RegisterWorkerRequestDto
                {
                    ProtocolVersion = BimBridgeProtocol.Version,
                    WorkerInstanceId = _workerInstanceId,
                    MachineName = Environment.MachineName,
                    ExtensionVersion = BimBridgeIdentity.AssemblyVersion,
                    BimSdkVersion = BimBridgeIdentity.BimSdkVersion,
                    Capabilities = _dispatcher.Capabilities,
                    AvailableModelKeys = _models.GetModelKeys(),
                    MaxParallelTasks = 1
                }).ConfigureAwait(false);
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
                await RegisterAsync().ConfigureAwait(false);
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
            var health = await _client.GetHealthAsync().ConfigureAwait(false);
            BimBridgeException.ThrowIfError(health);
            if (_registration != null)
            {
                var workers = await _client.GetWorkersAsync().ConfigureAwait(false);
                var worker = workers.FirstOrDefault(x => x.RegistrationId == _registration.RegistrationId);
                if (worker == null)
                {
                    _registration = null;
                    State.Update(s => s.With(
                        state: BimBridgeClientStates.Stopped,
                        registrationId: Guid.Empty,
                        isRegistered: false,
                        isRunning: false));
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
        }

        public string GetDiagnostics()
        {
            var s = State.Current;
            return
                $"State: {s.State}{Environment.NewLine}" +
                $"WorkerInstanceId: {s.WorkerInstanceId}{Environment.NewLine}" +
                $"RegistrationId: {s.RegistrationId:D}{Environment.NewLine}" +
                $"CurrentTaskId: {s.CurrentTaskId}{Environment.NewLine}" +
                $"Operation: {s.CurrentOperation}{Environment.NewLine}" +
                $"Progress: {s.ProgressPercent}%{Environment.NewLine}" +
                $"LastHeartbeatUtc: {s.LastHeartbeatUtc:O}{Environment.NewLine}" +
                $"LastError: {s.LastErrorCode} {s.LastError}{Environment.NewLine}" +
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
                    if (_registration == null) await RegisterAsync().ConfigureAwait(false);
                    State.Update(s => s.With(
                        state: BimBridgeClientStates.Waiting,
                        isRunning: true,
                        modelKeys: _models.GetModelKeys()));
                    var response = await _client.WaitForTaskAsync(
                        _registration.RegistrationId, _registration.LongPollingTimeoutSeconds).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    BimBridgeException.ThrowIfError(response);
                    reconnectAttempt = 0;
                    if (response.HasTask && response.Assignment != null)
                        await ExecuteAssignmentAsync(response.Assignment, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (BimBridgeException exception) when (
                    string.Equals(exception.Code, BimBridgeErrorCodes.WorkerNotRegistered, StringComparison.OrdinalIgnoreCase))
                {
                    _registration = null;
                    State.Update(s => s.With(registrationId: Guid.Empty, isRegistered: false));
                }
                catch (Exception exception)
                {
                    var bridge = exception as BimBridgeException ??
                                 new BimBridgeException(BimBridgeErrorCodes.InternalError, exception.Message, true, exception);
                    _log.Error("Worker loop failure.", bridge);
                    if (bridge.Code == BimBridgeErrorCodes.UnsupportedPilotVersion || !_settings.ReconnectEnabled)
                    {
                        SetError(bridge);
                        break;
                    }
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
            var registration = _registration;
            var start = await _client.StartTaskAsync(
                registration.RegistrationId, assignment.TaskId, assignment.AssignmentId).ConfigureAwait(false);
            BimBridgeException.ThrowIfError(start);
            BimBridgeException heartbeatFailure = null;
            using (var source = CancellationTokenSource.CreateLinkedTokenSource(runToken))
            {
                lock (_loopGate) _assignmentSource = source;
                State.Update(s => s.With(
                    state: BimBridgeClientStates.Executing,
                    currentTaskId: assignment.TaskId,
                    currentOperation: assignment.OperationCode,
                    progressPercent: 0));
                var heartbeatTask = HeartbeatLoopAsync(assignment, source, x => heartbeatFailure = x);
                try
                {
                    var resultObject = await _dispatcher.ExecuteAsync(new TaskExecutionContext
                    {
                        Assignment = assignment,
                        CancellationToken = source.Token,
                        ReportProgress = (percent, message) => State.Update(s => s.With(progressPercent: percent))
                    }).ConfigureAwait(false);
                    source.Token.ThrowIfCancellationRequested();
                    using (var result = resultObject as PreparedResult ?? await _resultWriter.WriteAsync(assignment.TaskId, resultObject, source.Token)
                               .ConfigureAwait(false))
                    {
                        await EnsureLeaseAsync(assignment, source.Token).ConfigureAwait(false);
                        State.Update(s => s.With(state: BimBridgeClientStates.Uploading, progressPercent: 0));
                        var uploader = new ResultUploader(_client);
                        await uploader.UploadAndCompleteAsync(
                            registration.RegistrationId,
                            assignment,
                            result,
                            registration.ResultChunkSizeBytes,
                            (percent, message) => State.Update(s => s.With(progressPercent: percent)),
                            source.Token).ConfigureAwait(false);
                    }
                    _log.Info($"Task completed: {assignment.TaskId}, operation={assignment.OperationCode}");
                }
                catch (OperationCanceledException)
                {
                    var failure = heartbeatFailure ??
                                  new BimBridgeException(BimBridgeErrorCodes.TaskCancelled, "Task was cancelled.");
                    await ReportFailureAsync(assignment, failure, true).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    var failure = exception as BimBridgeException ??
                                  new BimBridgeException(BimBridgeErrorCodes.InternalError, exception.Message, false, exception);
                    await ReportFailureAsync(assignment, failure, false).ConfigureAwait(false);
                }
                finally
                {
                    source.Cancel();
                    try { await heartbeatTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception exception) { _log.Warn("Heartbeat loop ended with: " + exception.Message); }
                    lock (_loopGate) if (ReferenceEquals(_assignmentSource, source)) _assignmentSource = null;
                    State.Update(s => s.With(
                        state: BimBridgeClientStates.Waiting,
                        clearCurrentTask: true,
                        progressPercent: 0));
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
            var response = await _client.HeartbeatAsync(new WorkerHeartbeatRequestDto
            {
                RegistrationId = _registration.RegistrationId,
                State = BimWorkerStates.Busy,
                CurrentTaskId = assignment.TaskId,
                AssignmentId = assignment.AssignmentId,
                AvailableModelKeys = _models.GetModelKeys(),
                Capabilities = _dispatcher.Capabilities,
                StatusMessage = $"{assignment.OperationCode}; progress={State.Current.ProgressPercent}%",
                ClientTimeUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
            State.Update(s => s.With(lastHeartbeatUtc: DateTime.UtcNow, modelKeys: _models.GetModelKeys()));
            return response;
        }

        private async Task ReportFailureAsync(BimTaskAssignmentDto assignment, BimBridgeException failure, bool cancelled)
        {
            _log.Error($"Task failed: {assignment.TaskId}, code={failure.Code}", failure);
            try
            {
                var response = await _client.FailTaskAsync(new FailTaskRequestDto
                {
                    RegistrationId = _registration.RegistrationId,
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
                }).ConfigureAwait(false);
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
                progressPercent: 0));
        }

        private async Task UnregisterCoreAsync()
        {
            var registration = _registration;
            _registration = null;
            if (registration != null && _client != null)
            {
                try { await _client.UnregisterWorkerAsync(registration.RegistrationId).ConfigureAwait(false); }
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

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BimBridgeWorkerController));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _models.CatalogChanged -= OnCatalogChanged;
            try { StopLoopAsync(true).GetAwaiter().GetResult(); }
            catch { }
            _runSource?.Dispose();
            _assignmentSource?.Dispose();
            _registrationGate.Dispose();
        }
    }
}
