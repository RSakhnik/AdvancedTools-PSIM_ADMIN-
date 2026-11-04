using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ascon.Pilot.Bim.SDK;

namespace AdvancedTools.BimBridge
{
    public class BulkSelectionRequest
    {
        public string TargetModelKey { get; set; }
        public DateTime? ModelVersionUtc { get; set; }
        public List<Guid> ModelPartIds { get; set; }
        public List<SearchConditionRequest> Conditions { get; set; } = new List<SearchConditionRequest>();
        public string LogicalOperator { get; set; } = "And";
        public List<ElementKey> ElementIds { get; set; }
    }

    // Shared selection for paged search and both bulk exports. Copy identifiers
    // in bounded SDK batches; property/geometry writers consume them immediately.
    public sealed class BulkSelectionReader
    {
        private readonly ModelContextProvider _models;
        private readonly BimBridgeSettings _settings;
        private readonly SnapshotElementIndexCache _snapshotIndex;
        private readonly SpatialPerformanceMetrics _performance;
        public BulkSelectionReader(ModelContextProvider models, BimBridgeSettings settings,SnapshotElementIndexCache snapshotIndex=null,
            SpatialPerformanceMetrics performance=null)
        { _models=models;_settings=settings;_snapshotIndex=snapshotIndex;_performance=performance; }

        public async Task<long> VisitAsync(ModelAccess model, BulkSelectionRequest request,
            Func<ElementKey,Task> visit, CancellationToken token)
        {
            long count=0;
            Func<ElementKey,Task> accept=async key =>
            {
                token.ThrowIfCancellationRequested();
                if(key==null || key.ModelPartId==Guid.Empty || key.ElementId==Guid.Empty)
                    throw new BimBridgeException("INVALID_REQUEST","Selection contains an invalid composite ID.");
                if(!_models.IsPartInScope(model.ModelId,model.TargetModelPartIds,key.ModelPartId))
                    throw new BimBridgeException("PART_NOT_AVAILABLE","Selection is outside the model scope.");
                if(++count>_settings.MaxExportElements)
                    throw new BimBridgeException("EXPORT_ELEMENT_LIMIT","Selection exceeds the configured element limit.");
                await visit(key).ConfigureAwait(false);
            };
            if(request.ElementIds!=null)
            {
                if(request.Conditions?.Count>0 || request.ModelPartIds?.Count>0)
                    throw new BimBridgeException("INVALID_REQUEST","Explicit ElementIds cannot be combined with search or part selection.");
                await _models.ResolveModelPartsAsync(model.ModelId,request.ElementIds.Select(x=>x.ModelPartId),true,token).ConfigureAwait(false);
                foreach(var key in request.ElementIds) await accept(key).ConfigureAwait(false);
                return count;
            }
            var parts=(request.ModelPartIds ?? new List<Guid>()).Distinct().ToArray();
            if(parts.Any(p=>p==Guid.Empty)) throw new BimBridgeException("INVALID_REQUEST","Empty ModelPartId.");
            if(parts.Length==0)
            {
                if(model.TargetModelPartIds.Count>0) parts=model.TargetModelPartIds.ToArray();
                else
                {
                    if(!_models.GetCatalogReadiness().IsComplete)
                        throw new BimBridgeException("MODEL_NOT_AVAILABLE","Whole-model export requires a complete parts catalog.",true);
                    parts=_models.GetKnownModelPartIds(model.ModelId);
                }
            }
            await _models.ResolveModelPartsAsync(model.ModelId,parts,true,token).ConfigureAwait(false);
            foreach(var part in parts)
            {
                if(!_models.IsPartInScope(model.ModelId,model.TargetModelPartIds,part) || !await model.HasPartAsync(part,token).ConfigureAwait(false))
                    throw new BimBridgeException("PART_NOT_AVAILABLE","Selected part is unavailable in this snapshot.");
                if(request.Conditions?.Count>0)
                {
                    ModelSnapshotPolicy.AssertSearchVersion(model.Version,model.InitialLatestVersion);
                    var state=SearchElementsOperationHandler.BuildSearchState(new SearchElementsRequest {
                        Conditions=request.Conditions,LogicalOperator=request.LogicalOperator});
                    using(var lease=await _models.GetSearchServiceAsync(new SearchTargetAccess(model.ModelId,new[]{part}),part,token).ConfigureAwait(false))
                    {
                        var cursor=await model.Sdk.ReadAsync(storage=>lease.Service.Search(state,int.MaxValue).GetEnumerator(),token).ConfigureAwait(false);
                        try
                        {
                            while(true)
                            {
                                var batch=await model.Sdk.ReadAsync(storage=>
                                {
                                    var values=new List<ElementKey>(64);
                                    while(values.Count<64 && cursor.MoveNext())
                                    {
                                        var key=new ElementKey {ModelPartId=cursor.Current.ModelPartId,ElementId=cursor.Current.ElementId};
                                        if(key.ModelPartId!=part) throw new BimBridgeException("SEARCH_INDEX_MISMATCH","Search returned another part.");
                                        SnapshotElementReader.AssertSearchElement(storage,key,model.Version);
                                        values.Add(key);
                                    }
                                    return values.ToArray();
                                },token).ConfigureAwait(false);
                                foreach(var key in batch) await accept(key).ConfigureAwait(false);
                                if(batch.Length<64) break;
                            }
                        }
                        finally { await model.Sdk.ReleaseOwnedAsync(cursor).ConfigureAwait(false); }
                    }
                }
                else if(_snapshotIndex!=null)
                {
                    // Selection and spatial existence share this ordered snapshot scan.
                    var index=await _snapshotIndex.GetAsync(model,part,token,_performance).ConfigureAwait(false);
                    foreach(var id in index.OrderedIds)await accept(new ElementKey{ModelPartId=part,ElementId=id}).ConfigureAwait(false);
                }
                else
                {
                    // SDK explicitly exposes all elements of a part. No GlobalId
                    // assumption and no Name/Defined filter is needed.
                    var cursor=await model.Sdk.ReadAsync(storage=>new SpatialSdkCursor<IModelElement>(storage.LoadElements(part,model.Version)),token).ConfigureAwait(false);
                    try
                    {
                        while(true)
                        {
                            var batch=await model.Sdk.ReadAsync(storage=>cursor.Read(64,token).Select(e=>new ElementKey {
                                ModelPartId=e.Id.ModelPartId,ElementId=e.Id.ElementId}).ToArray(),token).ConfigureAwait(false);
                            foreach(var key in batch) await accept(key).ConfigureAwait(false);
                            if(batch.Length<64) break;
                        }
                    }
                    finally { await model.Sdk.ReleaseOwnedAsync(cursor).ConfigureAwait(false); }
                }
            }
            return count;
        }
    }
}
