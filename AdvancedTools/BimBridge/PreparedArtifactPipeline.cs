using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
    // One delivery lane; durable spool is the queue and survives process restarts.
    public sealed class PreparedArtifactPipeline
    {
        private readonly DurableArtifactSpool _spool;
        private readonly BimBridgeSettings _settings;
        private readonly Func<DurableArtifact,CancellationToken,Task> _deliver;
        private readonly Action<Exception> _failure;
        private long _uploadTicks, _retryTicks, _sdkTicks;
        private readonly Stopwatch _lifetime=Stopwatch.StartNew();
        public PreparedArtifactPipeline(DurableArtifactSpool spool,BimBridgeSettings settings,
            Func<DurableArtifact,CancellationToken,Task> deliver,Action<Exception> failure)
        { _spool=spool;_settings=settings;_deliver=deliver;_failure=failure; }
        public int PreparedQueueDepth=>_spool.Pending().Length;
        public long PreparedQueueBytes=>_spool.Pending().Sum(a=>a.Descriptor.TotalLength);
        public double UploadBusyMs=>Interlocked.Read(ref _uploadTicks)*1000d/Stopwatch.Frequency;
        public double UploadRetryWaitMs=>Interlocked.Read(ref _retryTicks)*1000d/Stopwatch.Frequency;
        public double SdkBusyMs=>Interlocked.Read(ref _sdkTicks)*1000d/Stopwatch.Frequency;
        public double SdkIdleMs=>Math.Max(0,_lifetime.Elapsed.TotalMilliseconds-SdkBusyMs);
        public void RecordSdkTicks(long ticks)=>Interlocked.Add(ref _sdkTicks,ticks);
        public void RecordRetryMs(double ms)=>Interlocked.Add(ref _retryTicks,(long)(ms*Stopwatch.Frequency/1000d));
        public bool CanAccept
        {
            get
            {
                var pending=_spool.Pending();
                // Reserve worst-case artifact headroom before starting SDK work.
                // Includes the artifact currently being uploaded, not just queued ones.
                return pending.All(a=>a.ManagerPrepared) && pending.Length<Math.Max(1,_settings.MaxPreparedArtifacts)
                    && _spool.OccupiedBytes<=Math.Min(_settings.MaxPreparedArtifactBytes,
                        _settings.ArtifactDiskQuotaBytes)-_settings.MaxArtifactBytes;
            }
        }
        public async Task WaitForCapacityAsync(CancellationToken token)
        {while(!CanAccept)await Task.Delay(100,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();}
        public async Task RunAsync(CancellationToken token)
        {
            try {
            while(!token.IsCancellationRequested)
            {
                DurableArtifact current=null;
                try
                {
                    var artifact=_spool.Pending().OrderBy(a=>a.PreparedAtUtc).FirstOrDefault();
                    current=artifact;
                    if(artifact==null){await Task.Delay(100,token).ConfigureAwait(false);continue;}
                    var started=Stopwatch.GetTimestamp();
                    var retries=Interlocked.Read(ref _retryTicks);
                    try {await _deliver(artifact,token).ConfigureAwait(false);_spool.Remove(artifact);}
                    finally {Interlocked.Add(ref _uploadTicks,Math.Max(0,Stopwatch.GetTimestamp()-started-(Interlocked.Read(ref _retryTicks)-retries)));}
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                catch(Exception error)
                {
                    _failure?.Invoke(error);
                    var failure=error as BimBridgeException;
                    if(current!=null && failure!=null && new[]{"TASK_CANCELLED","TRANSFER_EXPIRED","TASK_ALREADY_COMPLETED",
                        "ASSIGNMENT_MISMATCH","INTEGRITY_ERROR","CHUNK_CONFLICT","RESULT_TOO_LARGE"}.Contains(failure.Code))
                    { _spool.Quarantine(current,failure.Code);continue; }
                    // Preserve bytes/state even for permanent errors. The bounded
                    // queue pauses extraction instead of deleting undelivered data.
                    var started=Stopwatch.GetTimestamp();
                    try{await Task.Delay(2000,token).ConfigureAwait(false);}
                    catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                    finally{Interlocked.Add(ref _retryTicks,Stopwatch.GetTimestamp()-started);}
                }
            }
            } finally {_lifetime.Stop();}
        }
    }
}
