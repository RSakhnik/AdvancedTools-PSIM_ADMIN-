using Ascon.Pilot.Bim.SDK;
using Ascon.Pilot.Bim.SDK.ModelStorage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
    // Owned by the SDK Dispatcher, including enumerator creation, MoveNext and Dispose.
    public sealed class SpatialSdkSource : ISpatialSource,ISpatialNodeSource
    {
        private readonly ModelAccess _model;
        private readonly bool _normals,_indices,_edges;
        private readonly SnapshotElementIndexCache _snapshotIndex;
        private readonly bool _coalescedNodeRead;
        private readonly MeshGeometryCache _meshCache;
        private readonly SpatialPerformanceMetrics _performance;
        private readonly Action<long> _recordAcquire,_recordVertices,_recordTraversal;
        private readonly Dictionary<Guid,SnapshotElementIndexCache.PartIndex> _partIndexes=new Dictionary<Guid,SnapshotElementIndexCache.PartIndex>();
        public SpatialSdkSource(ModelAccess model,bool normals=true,bool indices=true,bool edges=true,SnapshotElementIndexCache snapshotIndex=null,bool coalescedNodeRead=false,MeshGeometryCache meshCache=null,SpatialPerformanceMetrics performance=null)
        { _model=model;_normals=normals;_indices=indices;_edges=edges;_snapshotIndex=snapshotIndex;_coalescedNodeRead=coalescedNodeRead;_meshCache=meshCache;_performance=performance;_recordTraversal=performance==null?null:(Action<long>)performance.AddTraversal;_recordAcquire=performance==null?null:(Action<long>)performance.AddMeshAcquire;_recordVertices=performance==null?null:(Action<long>)performance.AddVertices; }
        public async Task<bool> ExistsAsync(ElementKey key,CancellationToken token)
        {
            if(_snapshotIndex!=null)
            {
                if(!_partIndexes.TryGetValue(key.ModelPartId,out var index))
                {index=await _snapshotIndex.GetAsync(_model,key.ModelPartId,token).ConfigureAwait(false);_partIndexes.Add(key.ModelPartId,index);}
                return index.Contains(key.ElementId);
            }
            return await _model.Sdk.ReadAsync(s=>
                Timed(()=>s.LoadElement(new ModelElementId(key.ModelPartId,key.ElementId),_model.Version)!=null,_recordTraversal),token).ConfigureAwait(false);
        }
        public bool CoalescedNodeReadEnabled=>_coalescedNodeRead;
        public Task<SpatialNodeSnapshot> ReadNodeAsync(ElementKey key,bool includeChildren,CancellationToken token)
        {
            if(!_coalescedNodeRead)throw new InvalidOperationException("Coalesced node reading is disabled.");
            return _model.Sdk.ReadAsync(storage=>
            {
                var started=Stopwatch.GetTimestamp();
                try
                {
                var identity=new ModelElementId(key.ModelPartId,key.ElementId);
                var instances=new List<SpatialInstance>();var children=new List<ElementKey>();
                // One owner-dispatcher scope per node. Copy all values to plain CLR
                // DTOs; SDK enumerators are disposed before the callback returns.
                using(var cursor=new SpatialSdkCursor<IMeshInstance>(storage.LoadElementMeshInstances(identity,_model.Version)))
                {
                    var batch=cursor.Read(16,token);
                    // A wide node falls back to the original bounded cursor path.
                    // No SDK cursor or object leaves this callback, and the fallback
                    // replays the complete node in its original order.
                    if(batch.Length==16)return null;
                    foreach(var item in batch)
                    {
                        var mesh=item.MeshDefinitionId;
                        instances.Add(new SpatialInstance{MeshId=mesh.MeshId,MeshPartId=mesh.ModelPartId,Placement=SpatialMath.Matrix(item.Placement)});
                    }
                }
                if(includeChildren)
                    using(var cursor=new SpatialSdkCursor<IModelElement>(storage.LoadChildElements(identity,_model.Version)))
                    {
                        var batch=cursor.Read(64,token);
                        if(batch.Length==64)return null;
                        foreach(var item in batch)
                        {var id=item.Id;children.Add(new ElementKey{ModelPartId=id.ModelPartId,ElementId=id.ElementId});}
                    }
                return new SpatialNodeSnapshot{Instances=instances.ToArray(),Children=children.ToArray()};
                }
                finally{_performance?.AddTraversal(Stopwatch.GetTimestamp()-started);}
            },token);
        }
        public async Task VisitChildrenAsync(ElementKey key,Func<ElementKey,bool> visit,CancellationToken token)
        {
            var cursor=await _model.Sdk.ReadAsync(s => new SpatialSdkCursor<IModelElement>(Timed(()=>s.LoadChildElements(new ModelElementId(key.ModelPartId,key.ElementId),_model.Version),_recordTraversal)),token).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var batch=await _model.Sdk.ReadAsync(s => Timed(()=>cursor.Read(64,token).Select(x => new ElementKey {ModelPartId=x.Id.ModelPartId,ElementId=x.Id.ElementId}).ToArray(),_recordTraversal),token).ConfigureAwait(false);
                    foreach(var item in batch) if(!visit(item)) return;
                    if(batch.Length<64) break;
                }
            }
            finally { await _model.Sdk.ReleaseOwnedAsync(cursor).ConfigureAwait(false); }
        }
        public async Task VisitInstancesAsync(ElementKey key,Func<SpatialInstance,Task> visit,CancellationToken token)
        {
            var cursor=await _model.Sdk.ReadAsync(s => new SpatialSdkCursor<IMeshInstance>(Timed(()=>s.LoadElementMeshInstances(new ModelElementId(key.ModelPartId,key.ElementId),_model.Version),_recordTraversal)),token).ConfigureAwait(false);
            try
            {
                while(true)
                {
                    var batch=await _model.Sdk.ReadAsync(s => Timed(()=>cursor.Read(16,token).Select(x => new SpatialInstance {
                        MeshId=x.MeshDefinitionId.MeshId,MeshPartId=x.MeshDefinitionId.ModelPartId,Placement=SpatialMath.Matrix(x.Placement)}).ToArray(),_recordTraversal),token).ConfigureAwait(false);
                    foreach(var item in batch) await visit(item).ConfigureAwait(false);
                    if(batch.Length<16) break;
                }
            }
            finally { await _model.Sdk.ReleaseOwnedAsync(cursor).ConfigureAwait(false); }
        }
        public async Task VisitMeshAsync(SpatialInstance instance,Func<string,double[],Task> floats,Func<string,uint[],Task> indices,CancellationToken token)
        {
            if(_meshCache!=null)
            {await VisitCachedMeshAsync(instance,floats,indices,token).ConfigureAwait(false);return;}
            // Mesh identity comes from this snapshot's instance, never from an independent lookup.
            var mesh=await _model.Sdk.ReadAsync(s => Timed(()=>s.LoadMeshDefinition(new SpatialMeshId(instance.MeshPartId,instance.MeshId)),_recordAcquire),token).ConfigureAwait(false);
            if(mesh==null) throw new BimBridgeException("GEOMETRY_READ_FAILED","Mesh definition is unavailable.");
            foreach(var field in new[]{"Vertices","Normals"})
            {
                if(field=="Normals"&&!_normals)continue;
                var cursor=await _model.Sdk.ReadAsync(s => new SpatialSdkCursor<float>(field=="Vertices"?mesh.Vertices:mesh.Normals),token).ConfigureAwait(false);
                try
                {
                    do
                    {
                        var batch=await _model.Sdk.ReadAsync(s => Timed(()=>cursor.Read(8190,token).Select(x=>(double)x).ToArray(),_recordVertices),token).ConfigureAwait(false);
                        await floats(field,batch).ConfigureAwait(false);
                        if(batch.Length<8190) break;
                    } while(true);
                }
                finally { await _model.Sdk.ReleaseOwnedAsync(cursor).ConfigureAwait(false); }
            }
            foreach(var field in new[]{"Indices","EdgeIndices"})
            {
                if(field=="Indices"&&!_indices || field=="EdgeIndices"&&!_edges)continue;
                var cursor=await _model.Sdk.ReadAsync(s => new SpatialSdkCursor<uint>(field=="Indices"?mesh.Indices:mesh.EdgeIndices),token).ConfigureAwait(false);
                try
                {
                    do
                    {
                        var batch=await _model.Sdk.ReadAsync(s => cursor.Read(8190,token),token).ConfigureAwait(false);
                        await indices(field,batch).ConfigureAwait(false);
                        if(batch.Length<8190) break;
                    } while(true);
                }
                finally { await _model.Sdk.ReleaseOwnedAsync(cursor).ConfigureAwait(false); }
            }
        }
        private async Task VisitCachedMeshAsync(SpatialInstance instance,Func<string,double[],Task> floats,
            Func<string,uint[],Task> indices,CancellationToken token)
        {
            // Version is part of the namespace: a definition from a newer model
            // snapshot must never be replayed into a pinned older task.
            var key=MeshGeometryCache.Key(_model,instance.MeshPartId,instance.MeshId);
            var found=_meshCache.Find(key,_normals,_indices,_edges);
            var geometry=found?.Clone()??new MeshGeometryCache.Geometry();
            IMeshDefinition mesh=null;
            if(!geometry.Has(_normals,_indices,_edges))
            {
                mesh=await _model.Sdk.ReadAsync(s=>Timed(()=>s.LoadMeshDefinition(new SpatialMeshId(instance.MeshPartId,instance.MeshId)),_recordAcquire),token).ConfigureAwait(false);
                if(mesh==null)throw new BimBridgeException("GEOMETRY_READ_FAILED","Mesh definition is unavailable.");
            }
            var cacheable=true;
            foreach(var field in new[]{"Vertices","Normals"})
            {
                if(field=="Normals"&&!_normals)continue;
                var saved=field=="Vertices"?geometry.Vertices:geometry.Normals;
                if(saved!=null)
                {foreach(var batch in saved){token.ThrowIfCancellationRequested();await floats(field,batch).ConfigureAwait(false);}continue;}
                var captured=new List<double[]>();long capturedBytes=geometry.Bytes;
                var cursor=await _model.Sdk.ReadAsync(s=>new SpatialSdkCursor<float>(field=="Vertices"?mesh.Vertices:mesh.Normals),token).ConfigureAwait(false);
                try
                {
                    while(true)
                    {
                        var batch=await _model.Sdk.ReadAsync(s=>Timed(()=>cursor.Read(8190,token).Select(x=>(double)x).ToArray(),_recordVertices),token).ConfigureAwait(false);
                        if(cacheable)
                        {
                            if(capturedBytes<=_meshCache.MaxEntryBytes-8L*batch.Length)
                            {captured.Add(batch);capturedBytes+=8L*batch.Length;}
                            else{cacheable=false;captured.Clear();}
                        }
                        await floats(field,batch).ConfigureAwait(false);
                        if(batch.Length<8190)break;
                    }
                }
                finally{await _model.Sdk.ReleaseOwnedAsync(cursor).ConfigureAwait(false);}
                if(cacheable){if(field=="Vertices")geometry.Vertices=captured;else geometry.Normals=captured;}
            }
            foreach(var field in new[]{"Indices","EdgeIndices"})
            {
                if(field=="Indices"&&!_indices||field=="EdgeIndices"&&!_edges)continue;
                var saved=field=="Indices"?geometry.Indices:geometry.Edges;
                if(saved!=null)
                {foreach(var batch in saved){token.ThrowIfCancellationRequested();await indices(field,batch).ConfigureAwait(false);}continue;}
                var captured=new List<uint[]>();long capturedBytes=geometry.Bytes;
                var cursor=await _model.Sdk.ReadAsync(s=>new SpatialSdkCursor<uint>(field=="Indices"?mesh.Indices:mesh.EdgeIndices),token).ConfigureAwait(false);
                try
                {
                    while(true)
                    {
                        var batch=await _model.Sdk.ReadAsync(s=>cursor.Read(8190,token),token).ConfigureAwait(false);
                        if(cacheable)
                        {
                            if(capturedBytes<=_meshCache.MaxEntryBytes-4L*batch.Length)
                            {captured.Add(batch);capturedBytes+=4L*batch.Length;}
                            else{cacheable=false;captured.Clear();}
                        }
                        await indices(field,batch).ConfigureAwait(false);
                        if(batch.Length<8190)break;
                    }
                }
                finally{await _model.Sdk.ReleaseOwnedAsync(cursor).ConfigureAwait(false);}
                if(cacheable){if(field=="Indices")geometry.Indices=captured;else geometry.Edges=captured;}
            }
            if(mesh!=null&&cacheable&&geometry.Has(_normals,_indices,_edges))
            {
                var vertexScalars=geometry.Vertices.Sum(x=>(long)x.Length);
                var normalScalars=geometry.Normals?.Sum(x=>(long)x.Length)??0;
                var indexScalars=geometry.Indices?.Sum(x=>(long)x.Length)??0;
                var edgeScalars=geometry.Edges?.Sum(x=>(long)x.Length)??0;
                // The writer performs the same checks after VisitMeshAsync. Validate
                // before publishing the cache entry so a failed artifact cannot
                // poison a later task in the same worker.
                if(vertexScalars%3!=0||(normalScalars!=0&&normalScalars!=vertexScalars)||
                    indexScalars%3!=0||edgeScalars%2!=0)
                    throw new BimBridgeException("INVALID_SPATIAL_DATA","Incomplete mesh vector/index arrays.");
                _meshCache.Put(key,geometry);
            }
        }
        private static T Timed<T>(Func<T> read,Action<long> record)
        {var started=Stopwatch.GetTimestamp();try{return read();}finally{record?.Invoke(Stopwatch.GetTimestamp()-started);}}
        private sealed class SpatialMeshId : IMeshDefinitionId
        {
            public SpatialMeshId(Guid part,Guid mesh) { ModelPartId=part;MeshId=mesh; }
            public Guid ModelPartId {get;}
            public Guid MeshId {get;}
        }
    }

}
