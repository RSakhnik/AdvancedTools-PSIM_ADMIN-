using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Pilot.BimBridge.Contracts;

namespace AdvancedTools.BimBridge
{
    public sealed class ExportSpatialOperationHandler:IBimOperationHandler,IModelKeyResolverHandler,IAdditionalCapabilities
    {
        private readonly ModelContextProvider _models;private readonly BimBridgeSettings _settings;private readonly ArtifactFileWriter _writer;private readonly string _scratch;
        private readonly SnapshotElementIndexCache _snapshotIndex;
        private readonly MeshGeometryCache _meshCache;
        private readonly SpatialPerformanceMetrics _performance=new SpatialPerformanceMetrics();
        public string GetPerformanceDiagnostics()=>_settings.TelemetryMode=="Off"?"Spatial telemetry: Off":"Spatial: sources="+_performance.SourceElements+"; nodes="+_performance.TraversedNodes+"; uniqueMeshes="+_performance.UniqueMeshes+"; instances="+_performance.Instances+
            "; index hits="+_snapshotIndex.Hits+"; misses="+_snapshotIndex.Misses+"; builds="+_snapshotIndex.Builds+"; evictions="+_snapshotIndex.Evictions+"; entries="+_snapshotIndex.Entries+"; bytes="+_snapshotIndex.Bytes+
            "; mesh hits="+_meshCache.Hits+"; misses="+_meshCache.Misses+"; hitRatio="+(_meshCache.Hits+_meshCache.Misses==0?0d:(double)_meshCache.Hits/(_meshCache.Hits+_meshCache.Misses)).ToString("P1")+"; evictions="+_meshCache.Evictions+"; entries="+_meshCache.Entries+"; bytes="+_meshCache.Bytes+
            "; traversalSdkMs="+_performance.SdkTraversalReadMs.ToString("F1")+"; meshAcquireMs="+_performance.SdkMeshAcquireMs.ToString("F1")+
            "; verticesSdkMs="+_performance.SdkVerticesReadMs.ToString("F1")+"; boundsCpuMs="+_performance.BoundsCpuMs.ToString("F1");
        public ExportSpatialOperationHandler(ModelContextProvider models,BimBridgeSettings settings,ArtifactFileWriter writer,string scratch)
        {_models=models;_settings=settings;_writer=writer;_scratch=scratch;
            _snapshotIndex=new SnapshotElementIndexCache(settings.SnapshotIndexMaxBytes,settings.SnapshotIndexMaxEntries);
            _meshCache=new MeshGeometryCache(settings.MeshCacheMaxBytes,settings.MeshCacheMaxEntries);}
        public IReadOnlyCollection<string> OperationCodes {get;}=new[]{"export-spatial"};
        public string Capability=>ArtifactProtocol.ExportSpatial;
        public IEnumerable<string> AdditionalCapabilities=>new[]{ArtifactProtocol.BinarySpatial};
        public bool IsAvailable=>_models.HasModelServices;
        public async Task<object> ExecuteAsync(TaskExecutionContext context)
        {
            var request=BimOperationDispatcher.ReadPayload<ExportSpatialRequest>(context.Assignment);
            request.TargetModelKey=request.TargetModelKey??context.Assignment.TargetModelKey;
            var token=context.CancellationToken;
            var taskPerformance=_settings.TelemetryMode=="Off"?null:
                new SpatialPerformanceMetrics(_settings.TelemetryMode=="Detailed");
            var accessStarted=taskPerformance==null?0:Stopwatch.GetTimestamp();
            var model=await _models.GetModelAsync(request.TargetModelKey,request.ModelVersionUtc,token).ConfigureAwait(false);
            if(taskPerformance!=null)taskPerformance.AddModelAccess(Stopwatch.GetTimestamp()-accessStarted);
            context.Resources?.Track(model);
            using(model)
            {
                PreparedResult result;
                try
                {
                    result=await _writer.WriteAsync(context.Assignment.TaskId,"application/vnd.pilot.bim-spatial;version=2","spatial.bbsp",false,
                        async (stream,cancellation)=>
                        {
                            var available=new HashSet<Guid>(await model.Sdk.ReadAsync(storage=>(storage.GetModelPartsIds()??Enumerable.Empty<Guid>()).ToArray(),cancellation).ConfigureAwait(false));
                            using(var index=new SpatialExportIndex(_scratch,_settings.SpatialScratchQuotaBytes))
                            using(var spatial=new BinarySpatialWriter(stream,new SpatialSdkSource(model,
                                request.IncludeMeshes&&request.IncludeNormals,request.IncludeMeshes&&request.IncludeIndices,request.IncludeMeshes&&request.IncludeEdgeIndices,
                                _settings.SnapshotIndexEnabled?_snapshotIndex:null,_settings.CoalescedNodeReadEnabled,
                                _settings.MeshCacheEnabled?_meshCache:null,taskPerformance),index,request,model.ModelId,model.ModelVersionUtc,
                                part=>available.Contains(part)&&_models.IsPartInScope(model.ModelId,model.TargetModelPartIds,part),_settings.MaxBulkSpatialNodes,cancellation,taskPerformance))
                            {
                                await new BulkSelectionReader(_models,_settings,_settings.SnapshotIndexEnabled?_snapshotIndex:null,taskPerformance).VisitAsync(model,request,spatial.WriteSourceAsync,cancellation).ConfigureAwait(false);
                                await model.AssertUnchangedAsync(cancellation).ConfigureAwait(false);
                                spatial.Complete();
                            }
                        },token,taskPerformance).ConfigureAwait(false);
                    result.SpatialPerformance=taskPerformance?.Snapshot(context.Assignment.TaskId,
                        context.Assignment.AssignmentId,context.Assignment.AttemptNumber);
                    if(result.SpatialPerformance!=null)
                    {
                        var profile=result.SpatialPerformance;
                        if(_settings.MeshCacheEnabled)
                        {profile.WorkerMeshCacheEntries=_meshCache.Entries;profile.WorkerMeshCacheBytes=_meshCache.Bytes;
                            profile.WorkerMeshCacheEvictions=_meshCache.Evictions;}
                        if(_settings.SnapshotIndexEnabled)
                        {profile.WorkerSnapshotIndexEntries=_snapshotIndex.Entries;profile.WorkerSnapshotIndexBytes=_snapshotIndex.Bytes;
                            profile.WorkerSnapshotIndexEvictions=_snapshotIndex.Evictions;}
                    }
                }
                catch(SqliteException error) when(error.SqliteErrorCode==13)
                {throw new BimBridgeException("RESOURCE_LIMIT","Spatial deduplication/traversal scratch quota exceeded.");}
                finally {_performance.Add(taskPerformance);}
                try {await model.AssertUnchangedAsync(token).ConfigureAwait(false);return result;}
                catch {result.Dispose();throw;}
            }
        }
    }
}
