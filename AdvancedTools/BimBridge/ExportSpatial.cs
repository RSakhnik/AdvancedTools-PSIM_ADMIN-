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
        public ExportSpatialOperationHandler(ModelContextProvider models,BimBridgeSettings settings,ArtifactFileWriter writer,string scratch)
        {_models=models;_settings=settings;_writer=writer;_scratch=scratch;}
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
                                request.IncludeMeshes&&request.IncludeNormals,request.IncludeMeshes&&request.IncludeIndices,request.IncludeMeshes&&request.IncludeEdgeIndices),index,request,model.ModelId,model.ModelVersionUtc,
                                part=>available.Contains(part)&&_models.IsPartInScope(model.ModelId,model.TargetModelPartIds,part),_settings.MaxBulkSpatialNodes,cancellation))
                            {
                                await new BulkSelectionReader(_models,_settings).VisitAsync(model,request,spatial.WriteSourceAsync,cancellation).ConfigureAwait(false);
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
