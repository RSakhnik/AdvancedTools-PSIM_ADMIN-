using Newtonsoft.Json;
using Pilot.BimBridge.Contracts;
using System;
using System.IO;
using System.IO.Compression;
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
        public SpatialTaskPerformanceDto SpatialPerformance { get; set; }
        public bool DeleteOnDispose { get; set; }

        public void Dispose()
        {
            if (!DeleteOnDispose || string.IsNullOrWhiteSpace(Path)) return;
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

}
