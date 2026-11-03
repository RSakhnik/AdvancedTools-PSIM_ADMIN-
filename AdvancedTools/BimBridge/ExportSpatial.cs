using System;
using System.Collections.Generic;
using System.Linq;
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
        public string GetPerformanceDiagnostics()=>"Spatial: sources="+_performance.SourceElements+"; nodes="+_performance.TraversedNodes+"; uniqueMeshes="+_performance.UniqueMeshes+"; instances="+_performance.Instances+
            "; index hits="+_snapshotIndex.Hits+"; misses="+_snapshotIndex.Misses+"; builds="+_snapshotIndex.Builds+"; bytes="+_snapshotIndex.Bytes+
            "; mesh hits="+_meshCache.Hits+"; misses="+_meshCache.Misses+"; hitRatio="+(_meshCache.Hits+_meshCache.Misses==0?0d:(double)_meshCache.Hits/(_meshCache.Hits+_meshCache.Misses)).ToString("P1")+"; entries="+_meshCache.Entries+"; bytes="+_meshCache.Bytes+
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
            var model=await _models.GetModelAsync(request.TargetModelKey,request.ModelVersionUtc,token).ConfigureAwait(false);
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
                                _settings.MeshCacheEnabled?_meshCache:null,_performance),index,request,model.ModelId,model.ModelVersionUtc,
                                part=>available.Contains(part)&&_models.IsPartInScope(model.ModelId,model.TargetModelPartIds,part),_settings.MaxBulkSpatialNodes,cancellation,_performance))
                            {
                                await new BulkSelectionReader(_models,_settings,_settings.SnapshotIndexEnabled?_snapshotIndex:null).VisitAsync(model,request,spatial.WriteSourceAsync,cancellation).ConfigureAwait(false);
                                await model.AssertUnchangedAsync(cancellation).ConfigureAwait(false);
                                spatial.Complete();
                            }
                        },token).ConfigureAwait(false);
                }
                catch(SqliteException error) when(error.SqliteErrorCode==13)
                {throw new BimBridgeException("RESOURCE_LIMIT","Spatial deduplication/traversal scratch quota exceeded.");}
                try {await model.AssertUnchangedAsync(token).ConfigureAwait(false);return result;}
                catch {result.Dispose();throw;}
            }
        }
    }
}
