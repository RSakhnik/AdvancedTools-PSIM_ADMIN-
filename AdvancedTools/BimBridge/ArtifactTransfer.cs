using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Pilot.BimBridge.Contracts;

namespace AdvancedTools.BimBridge
{
    public interface IBimBridgeArtifactClient
    {
        Task<ArtifactUploadStatusDto> ReserveArtifactAsync(ReserveArtifactDto request);
        Task<ArtifactUploadStatusDto> BeginOrResumeAsync(BeginArtifactUploadDto request);
        Task<ArtifactUploadStatusDto> UploadStatusAsync(ArtifactTransferRequestDto request);
        Task<ArtifactChunkResponseDto> WriteArtifactChunkAsync(ArtifactChunkRequestDto request);
        Task<CompleteTaskResponseDto> CompleteArtifactAsync(ArtifactTransferRequestDto request);
    }
    public sealed class DurableArtifact
    {
        public Guid TransferId { get; set; }
        public string WorkerInstanceId { get; set; }
        public string Path { get; set; }
        public DateTime PreparedAtUtc { get; set; }
        public bool ManagerPrepared { get; set; }
        public double? WorkerProcessingWallMs {get;set;}
        public int? UploadRetryCount {get;set;}
        public double? UploadRetryWaitMs {get;set;}
        public SpatialTaskPerformanceDto SpatialPerformance {get;set;}
        public string DeliveryErrorCode {get;set;}
        public BeginResultUploadRequestDto Descriptor { get; set; }
        [JsonIgnore] public string StatePath { get; set; }
    }

    // Outside WorkerTemporaryWorkspace: network loss and process shutdown must
    // not delete prepared artifacts. Only confirmed Complete or retention does.
    public sealed class DurableArtifactSpool : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _root, _owner;
        private readonly BimBridgeSettings _settings;
        private readonly IBimBridgeLog _log;
        private readonly FileStream _ownerLock;
        public DurableArtifactSpool(BimBridgeSettings settings, string owner, IBimBridgeLog log)
        {
            _settings = settings; _owner = owner; _log = log;
            _root = System.IO.Path.Combine(settings.ResultsDirectory, "durable", settings.DurableWorkerId);
            Directory.CreateDirectory(_root);
            try { _ownerLock=new FileStream(System.IO.Path.Combine(_root,"owner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose); }
            catch(IOException) {throw new BimBridgeException("WORKER_IDENTITY_IN_USE","Another process owns this durable worker identity.");}
        }
        private static void Save(DurableArtifact artifact)
        {
            var temp = artifact.StatePath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(artifact));
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
            if(File.Exists(artifact.StatePath))File.Replace(temp,artifact.StatePath,null);
            else File.Move(temp, artifact.StatePath);
        }
        public DurableArtifact Reserve(BimTaskAssignmentDto assignment)
        {
            var id=Guid.NewGuid();
            var artifact=new DurableArtifact {TransferId=id,WorkerInstanceId=_owner,PreparedAtUtc=DateTime.UtcNow,
                UploadRetryCount=0,UploadRetryWaitMs=0,
                Path=System.IO.Path.Combine(_root,id.ToString("N")+".artifact"),
                StatePath=System.IO.Path.Combine(_root,id.ToString("N")+".reservation"),
                Descriptor=new BeginResultUploadRequestDto {TaskId=assignment.TaskId,AssignmentId=assignment.AssignmentId}};
            Save(artifact);return artifact;
        }
        public DurableArtifact Prepare(PreparedResult result, BimTaskAssignmentDto assignment, int chunkSize,DurableArtifact reserved=null,double? processingWallMs=null)
        {
            lock(_gate)
            {
                var started=result.SpatialPerformance==null?0:Stopwatch.GetTimestamp();
                var artifact=PrepareCore(result,assignment,chunkSize,reserved,processingWallMs);
                if(artifact.SpatialPerformance!=null)
                {
                    artifact.SpatialPerformance.SpoolPreparationWallMs=
                        (Stopwatch.GetTimestamp()-started)*1000d/Stopwatch.Frequency;
                    // The artifact and its first durable state are committed already.
                    // A telemetry checkpoint failure must not invalidate that commit.
                    try { Save(artifact); }
                    catch(Exception error) { _log?.Warn("Spool telemetry checkpoint failed: "+error.GetType().Name); }
                }
                return artifact;
            }
        }
        private DurableArtifact PrepareCore(PreparedResult result, BimTaskAssignmentDto assignment, int chunkSize,DurableArtifact reserved=null,double? processingWallMs=null)
        {
            if (chunkSize <= 0) throw new BimBridgeException("PROTOCOL_ERROR", "Invalid artifact chunk size.");
            if (result.Length < 0 || result.Length > _settings.MaxArtifactBytes)
                throw new BimBridgeException("RESULT_TOO_LARGE", "Artifact exceeds the configured byte limit.");
            var occupied = Directory.EnumerateFiles(_root, "*.artifact").Sum(path => new FileInfo(path).Length);
            if (occupied > _settings.ArtifactDiskQuotaBytes - result.Length)
                throw new BimBridgeException("RESOURCE_LIMIT", "Durable artifact disk quota exceeded.");
            var id = reserved?.TransferId??Guid.NewGuid();
            var artifact = new DurableArtifact { TransferId = id, WorkerInstanceId = _owner, PreparedAtUtc = DateTime.UtcNow, WorkerProcessingWallMs=processingWallMs,
                UploadRetryCount=reserved?.UploadRetryCount??0,UploadRetryWaitMs=reserved?.UploadRetryWaitMs??0,
                SpatialPerformance=result.SpatialPerformance,
                Path = System.IO.Path.Combine(_root, id.ToString("N") + ".artifact"),
                StatePath = System.IO.Path.Combine(_root, id.ToString("N") + ".json"),
                Descriptor = new BeginResultUploadRequestDto { TaskId = assignment.TaskId, AssignmentId = assignment.AssignmentId,
                    TotalLength = result.Length, TotalChunks = checked((int)(result.Length / chunkSize + (result.Length % chunkSize == 0 ? 0 : 1))),
                    Sha256 = result.Sha256, ContentType = result.ContentType, ContentEncoding = result.ContentEncoding,
                    FileName = result.FileName, ResultKind = result.ResultKind } };
            if(reserved!=null)
            {
                // Commit final descriptor to the intent before moving the bytes.
                // Recovery can finish the state publication after a crash in the
                // small interval between the atomic move and Save below.
                var intent=artifact.StatePath;
                artifact.StatePath=reserved.StatePath;
                var temp=reserved.StatePath+".update";
                File.WriteAllText(temp,JsonConvert.SerializeObject(artifact));
                using(var stream=new FileStream(temp,FileMode.Open,FileAccess.ReadWrite,FileShare.None))stream.Flush(true);
                File.Replace(temp,reserved.StatePath,null);
                artifact.StatePath=intent;
            }
            // Local move preserves the already formed artifact; no second SDK pass.
            File.Move(result.Path, artifact.Path);
            using(var durable=new FileStream(artifact.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))durable.Flush(true);
            result.DeleteOnDispose = false;
            Save(artifact);
            if(reserved!=null)File.Delete(reserved.StatePath);
            _log?.Info($"Artifact prepared: transfer={id}; task={assignment.TaskId}; bytes={result.Length}.");
            return artifact;
        }
        public DurableArtifact[] Pending()
        { lock(_gate) return PendingCore(); }
        private DurableArtifact[] PendingCore()
        {
            var retention=DateTime.UtcNow-TimeSpan.FromHours(_settings.ArtifactRetentionHours);
            foreach(var orphan in Directory.EnumerateFiles(_root,"*.artifact"))
            {
                var stem=System.IO.Path.Combine(_root,System.IO.Path.GetFileNameWithoutExtension(orphan));
                if(!File.Exists(stem+".json")&&!File.Exists(stem+".reservation")&&File.GetLastWriteTimeUtc(orphan)<retention)
                { _log?.Warn("Expired orphan artifact removed: "+System.IO.Path.GetFileName(orphan));File.Delete(orphan); }
            }
            foreach(var intent in Directory.EnumerateFiles(_root,"*.reservation"))
            {
                var artifact=JsonConvert.DeserializeObject<DurableArtifact>(File.ReadAllText(intent));
                if(artifact==null)throw new BimBridgeException("INTEGRITY_ERROR","Invalid artifact reservation.");
                if(File.Exists(artifact.Path) && !string.IsNullOrEmpty(artifact.Descriptor.Sha256))
                {
                    artifact.StatePath=System.IO.Path.Combine(_root,artifact.TransferId.ToString("N")+".json");
                    if(!File.Exists(artifact.StatePath))Save(artifact);
                    File.Delete(intent);
                }
                else if(DateTime.UtcNow-artifact.PreparedAtUtc>TimeSpan.FromHours(_settings.ArtifactRetentionHours))File.Delete(intent);
            }
            var result = new List<DurableArtifact>();
            foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
            {
                var artifact = JsonConvert.DeserializeObject<DurableArtifact>(File.ReadAllText(path));
                if (artifact == null || artifact.Descriptor==null || artifact.TransferId == Guid.Empty || artifact.WorkerInstanceId != _owner ||
                    System.IO.Path.GetFullPath(artifact.Path) != System.IO.Path.Combine(_root, artifact.TransferId.ToString("N") + ".artifact"))
                    throw new BimBridgeException("INTEGRITY_ERROR", "Invalid durable artifact state.");
                artifact.StatePath = path;
                if (DateTime.UtcNow - artifact.PreparedAtUtc > TimeSpan.FromHours(_settings.ArtifactRetentionHours))
                {
                    _log?.Warn($"Artifact retention expired: transfer={artifact.TransferId}; task={artifact.Descriptor.TaskId}.");
                    Remove(artifact); continue;
                }
                if (!File.Exists(artifact.Path) || new FileInfo(artifact.Path).Length != artifact.Descriptor.TotalLength)
                    throw new BimBridgeException("INTEGRITY_ERROR", "Durable artifact is missing or has a different size.");
                result.Add(artifact);
            }
            return result.ToArray();
        }
        public long OccupiedBytes
        {get {lock(_gate)return Directory.EnumerateFiles(_root,"*.artifact").Sum(path=>new FileInfo(path).Length);}}
        public int FailedDeliveries
        {get {lock(_gate)return Directory.EnumerateFiles(_root,"*.failed").Count();}}
        public void Quarantine(DurableArtifact artifact,string code)
        {
            lock(_gate)
            {
                // Retain immutable bytes and descriptor for inspection. Terminal
                // transfers leave the runnable queue without silently deleting data.
                artifact.DeliveryErrorCode=code; Save(artifact);
                var target=System.IO.Path.ChangeExtension(artifact.StatePath,".failed");
                File.Move(artifact.StatePath,target);
                _log?.Warn("Artifact delivery terminal; retained for inspection: "+artifact.TransferId+"; code="+code);
            }
        }
        public bool IsQuarantined(DurableArtifact artifact)
        {lock(_gate)return File.Exists(System.IO.Path.ChangeExtension(artifact.StatePath,".failed"));}
        public void MarkPrepared(DurableArtifact artifact)
        { lock(_gate) { if(File.Exists(artifact.StatePath)&&File.Exists(artifact.Path)) {artifact.ManagerPrepared=true; Save(artifact);} } }
        public void RecordRetry(DurableArtifact artifact,double waitMs)
        {
            if(artifact==null || double.IsNaN(waitMs) || double.IsInfinity(waitMs) || waitMs<0)return;
            lock(_gate)
            {
                artifact.UploadRetryCount=(artifact.UploadRetryCount??0)+1;
                artifact.UploadRetryWaitMs=(artifact.UploadRetryWaitMs??0)+waitMs;
                try { if(File.Exists(artifact.StatePath))Save(artifact); }
                catch(Exception error) { _log?.Warn("Upload retry telemetry checkpoint failed: "+error.GetType().Name); }
            }
        }
        public void Remove(DurableArtifact artifact)
        { lock(_gate) RemoveCore(artifact); }
        private void RemoveCore(DurableArtifact artifact)
        {
            // Delete state first only after confirmed server completion. An orphan
            // data file can be retained, but cannot cause another SDK extraction.
            File.Delete(artifact.StatePath);
            File.Delete(artifact.Path);
        }
        public void Dispose()=>_ownerLock.Dispose();
    }

    public sealed class ResumableArtifactUploader
    {
        private readonly Action<double> _retryWait;
        private readonly Action<DurableArtifact,double> _artifactRetryWait;
        private readonly IBimBridgeArtifactClient _client;
        private readonly BimBridgeSettings _settings;
        private readonly IBimBridgeLog _log;
        public ResumableArtifactUploader(IBimBridgeArtifactClient client, BimBridgeSettings settings, IBimBridgeLog log,
            Action<double> retryWait=null,Action<DurableArtifact,double> artifactRetryWait=null)
        { _client = client; _settings = settings; _log = log; _retryWait=retryWait; _artifactRetryWait=artifactRetryWait; }

        private async Task<T> Rpc<T>(Func<Task<T>> invoke, CancellationToken token,DurableArtifact artifact=null) where T : BimBridgeResponseDto
        {
            var attempts = Math.Max(1, _settings.ResultTransferMaxAttempts);
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var pending = invoke();
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        if (await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(Math.Max(1,_settings.ResultTransferTimeoutSeconds)), timeout.Token)).ConfigureAwait(false) != pending)
                        {
                            _ = pending.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                            token.ThrowIfCancellationRequested();
                            throw new BimBridgeException("TRANSPORT_TIMEOUT", "Artifact RPC timed out.", true);
                        }
                        timeout.Cancel();
                        var result = await pending.ConfigureAwait(false);
                        BimBridgeException.ThrowIfError(result);
                        return result;
                    }
                }
                catch (Exception error) when (!(error is OperationCanceledException))
                {
                    var failure = TransportFailurePolicy.Classify(error);
                    if (!failure.IsRetryable || attempt + 1 >= attempts) throw failure;
                    var jitter = 0.8 + new Random(Guid.NewGuid().GetHashCode()).NextDouble() * 0.4;
                    _log?.Warn($"Artifact RPC retry: code={failure.Code}; attempt={attempt+1}.");
                    var retryWatch=Stopwatch.StartNew();
                    try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10000, 500*Math.Pow(2,attempt))*jitter),token).ConfigureAwait(false); }
                    finally
                    {
                        var waitMs=retryWatch.Elapsed.TotalMilliseconds;
                        _retryWait?.Invoke(waitMs);
                        _artifactRetryWait?.Invoke(artifact,waitMs);
                    }
                }
            }
        }

        public async Task PrepareAsync(DurableArtifact artifact,Guid registration,CancellationToken token)
        {
            artifact.Descriptor.RegistrationId=registration;
            await Rpc(()=>_client.BeginOrResumeAsync(BeginRequest(artifact)),token,artifact).ConfigureAwait(false);
        }

        private static BeginArtifactUploadDto BeginRequest(DurableArtifact artifact)=>new BeginArtifactUploadDto
        {
            TransferId=artifact.TransferId,WorkerInstanceId=artifact.WorkerInstanceId,
            Descriptor=artifact.Descriptor,WorkerProcessingWallMs=artifact.WorkerProcessingWallMs,
            SpatialPerformance=artifact.SpatialPerformance
        };

        public async Task UploadAsync(DurableArtifact artifact, Guid registration, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            artifact.Descriptor.RegistrationId = registration;
            var identity = new ArtifactTransferRequestDto { TransferId = artifact.TransferId, WorkerInstanceId = artifact.WorkerInstanceId, RegistrationId = registration };
            var status = await Rpc(() => _client.BeginOrResumeAsync(BeginRequest(artifact)),token,artifact).ConfigureAwait(false);
            if (!status.Completed)
            {
                status = await Rpc(() => _client.UploadStatusAsync(identity),token,artifact).ConfigureAwait(false);
                if (status.ChunkSizeBytes <= 0 || status.ChunkSizeBytes > _settings.MaxUploadChunkBytes || artifact.Descriptor.TotalChunks !=
                    artifact.Descriptor.TotalLength / status.ChunkSizeBytes + (artifact.Descriptor.TotalLength % status.ChunkSizeBytes == 0 ? 0 : 1))
                    throw new BimBridgeException("PROTOCOL_ERROR", "Server returned conflicting chunk geometry.");
                long sent = 0; var requests = 0;
                var inFlight = new List<Task>();
                using (var source = new FileStream(artifact.Path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true))
                {
                    try
                    {
                        var previousEnd = 0;
                        foreach (var range in status.MissingRanges ?? throw new BimBridgeException("PROTOCOL_ERROR","Missing ranges absent."))
                        {
                            if (range.Start < previousEnd || range.Count <= 0 || (long)range.Start+range.Count > artifact.Descriptor.TotalChunks)
                                throw new BimBridgeException("PROTOCOL_ERROR","Invalid missing chunk ranges.");
                            previousEnd = range.Start+range.Count;
                            for (var index = range.Start; index < previousEnd; index++)
                            {
                                token.ThrowIfCancellationRequested();
                                while (inFlight.Count >= Math.Max(1,Math.Min(4,_settings.MaxInFlightChunks)))
                                { var done = await Task.WhenAny(inFlight).ConfigureAwait(false); inFlight.Remove(done); await done.ConfigureAwait(false); }
                                source.Position = (long)index*status.ChunkSizeBytes;
                                var bytes = new byte[(int)Math.Min(status.ChunkSizeBytes,source.Length-source.Position)];
                                var read = 0;
                                while (read < bytes.Length)
                                { var n = await source.ReadAsync(bytes,read,bytes.Length-read,token).ConfigureAwait(false); if(n==0) throw new EndOfStreamException();read+=n; }
                                string hash; using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
                                // Each RPC/retry owns its immutable buffer. A local
                                // timeout never permits mutation of a pending call.
                                var request = new ArtifactChunkRequestDto {Transfer=identity,ChunkIndex=index,Data=bytes,Sha256=hash};
                                inFlight.Add(Rpc(() => _client.WriteArtifactChunkAsync(request),token,artifact));
                                sent += bytes.Length; requests++;
                            }
                        }
                        await Task.WhenAll(inFlight).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Observe outstanding tasks on any cancellation/failure.
                        try { await Task.WhenAll(inFlight).ConfigureAwait(false); } catch { /* original failure is propagated */ }
                    }
                }
                _log?.Info($"Artifact chunks sent: transfer={artifact.TransferId}; bytes={sent}; resumedBytes={artifact.Descriptor.TotalLength-sent}; requests={requests}; elapsedMs={watch.ElapsedMilliseconds}.");
            }
            identity.UploadWallMs=watch.Elapsed.TotalMilliseconds;
            identity.UploadRetryCount=artifact.UploadRetryCount;
            identity.UploadRetryWaitMs=artifact.UploadRetryWaitMs;
            await Rpc(() => _client.CompleteArtifactAsync(identity),token,artifact).ConfigureAwait(false);
            _log?.Info($"Artifact delivery confirmed: transfer={artifact.TransferId}; elapsedMs={watch.ElapsedMilliseconds}.");
        }
        public Task<ArtifactUploadStatusDto> ReserveAsync(DurableArtifact artifact,Guid registration,CancellationToken token)
            => Rpc(()=>_client.ReserveArtifactAsync(new ReserveArtifactDto {TaskId=artifact.Descriptor.TaskId,
                AssignmentId=artifact.Descriptor.AssignmentId,Transfer=new ArtifactTransferRequestDto {TransferId=artifact.TransferId,
                    WorkerInstanceId=artifact.WorkerInstanceId,RegistrationId=registration}}),token,artifact);
    }
}
