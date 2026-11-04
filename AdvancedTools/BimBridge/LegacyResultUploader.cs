using Pilot.BimBridge.Contracts;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
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

}
