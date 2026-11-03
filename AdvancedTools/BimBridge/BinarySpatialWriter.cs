using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AdvancedTools.BimBridge
{
    public sealed class ExportSpatialRequest:BulkSelectionRequest
    {
        public bool IncludeDescendants {get;set;}
        public bool IncludeMeshes {get;set;}=true;
        public bool IncludeNormals {get;set;}
        public bool IncludeIndices {get;set;}=true;
        public bool IncludeEdgeIndices {get;set;}
    }

    // BBSPAT2: 8-byte magic followed by (uint32 JSON length, uint64 blob
    // length, UTF8 metadata, blob) frames. Geometry is little-endian f32/u32
    // in bounded blocks, never JSON numeric arrays. A summary frame closes a
    // complete artifact. Per-block SHA256 permits verification while streaming.
    public sealed class BinarySpatialWriter:IDisposable
    {
        private readonly BinaryWriter _writer;
        private readonly ISpatialSource _source;
        private readonly SpatialExportIndex _index;
        private readonly ExportSpatialRequest _request;
        private readonly Func<Guid,bool> _partAllowed;
        private readonly long _nodeLimit;
        private readonly CancellationToken _token;
        private readonly string _version;
        private long _sources,_nodes,_instances,_meshes,_failed;
        private readonly SpatialPerformanceMetrics _performance;
        private readonly HashSet<string> _seenMeshKeys=new HashSet<string>(StringComparer.Ordinal);
        public BinarySpatialWriter(Stream output,ISpatialSource source,SpatialExportIndex index,ExportSpatialRequest request,
            Guid model,string version,Func<Guid,bool> partAllowed,long nodeLimit,CancellationToken token,SpatialPerformanceMetrics performance=null)
        {
            _writer=new BinaryWriter(output,new UTF8Encoding(false),true);_source=source;_index=index;_request=request;
            _partAllowed=partAllowed;_nodeLimit=nodeLimit;_token=token;_version=version;_performance=performance;
            _writer.Write(Encoding.ASCII.GetBytes("BBSPAT2\n"));
            Frame(new {Type="manifest",Format="bim-spatial",FormatVersion=2,ModelKey=model,ModelVersionUtc=version,
                CoordinateSpace="sdk-placement",SceneCoordinatesVerified=false,
                MatrixConvention=new {Storage="row-major",Vector="column",VertexSpace="mesh-local"},
                request.IncludeMeshes,request.IncludeNormals,request.IncludeIndices,request.IncludeEdgeIndices,request.IncludeDescendants});
        }
        private void Frame(object metadata,byte[] blob=null)
        {
            _token.ThrowIfCancellationRequested();
            var json=Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(metadata));
            if(json.Length>1048576)throw new BimBridgeException("RESULT_TOO_LARGE","Spatial frame metadata is too large.");
            _writer.Write((uint)json.Length);_writer.Write((ulong)(blob?.Length??0));_writer.Write(json);
            if(blob!=null)_writer.Write(blob);
        }
        private void Buffer(string key,string field,string dtype,long offset,byte[] blob,int count)
        {
            string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(blob)).Replace("-","").ToLowerInvariant();
            Frame(new {Type="buffer",MeshDefinitionKey=key,Field=field,DType=dtype,Offset=offset,Count=count,Sha256=hash},blob);
        }
        public async Task WriteSourceAsync(ElementKey root)
        {
            _sources++;_performance?.Source();_index.BeginRoot(root);
            Frame(new {Type="source-begin",Element=root,Scope=_request.IncludeDescendants?"subtree":"self"});
            var bounds=new SpatialBounds();long nodes=0,instances=0;
            var failuresBefore=_failed;
            ElementKey node;
            while((node=_index.NextNode())!=null)
            {
                _token.ThrowIfCancellationRequested();
                if(++_nodes>_nodeLimit)throw new BimBridgeException("EXPORT_TRAVERSAL_LIMIT","Bulk spatial traversal limit reached.");
                nodes++;_performance?.Node();
                if(!_partAllowed(node.ModelPartId))throw new BimBridgeException("PART_NOT_AVAILABLE","Spatial node is outside the selected model scope.");
                if(!await _source.ExistsAsync(node,_token).ConfigureAwait(false))
                {
                    _failed++;Frame(new {Type="error",Element=node,Code="ELEMENT_NOT_FOUND",Message="Element is absent from the pinned snapshot."});continue;
                }
                var current=node;
                var nodeSnapshot=_source is ISpatialNodeSource nodeSource && nodeSource.CoalescedNodeReadEnabled
                    ? await nodeSource.ReadNodeAsync(current,_request.IncludeDescendants,_token).ConfigureAwait(false) : null;
                Func<SpatialInstance,Task> visitInstance=async instance=>
                {
                    if(!_partAllowed(instance.MeshPartId)||instance.MeshId==Guid.Empty)
                        throw new BimBridgeException("INVALID_SPATIAL_DATA","Mesh identity is outside the model scope.");
                    SpatialMath.ValidateAffine(instance.Placement);
                    var key=instance.MeshPartId.ToString("D")+"/"+instance.MeshId.ToString("D");
                    if(_performance!=null&&_seenMeshKeys.Add(key))_performance.UniqueMesh();
                    var first=_request.IncludeMeshes&&_index.AddMesh(key);
                    if(first) {Frame(new {Type="mesh-begin",MeshDefinitionKey=key,ModelPartId=instance.MeshPartId,instance.MeshId});_meshes++;}
                    long vertices=0,normals=0,indices=0,edges=0;
                    await _source.VisitMeshAsync(instance,(field,values)=>
                    {
                        if(values.Length%3!=0)throw new BimBridgeException("INVALID_SPATIAL_DATA","Invalid spatial vector triples.");
                        var offset=field=="Vertices"?vertices:normals;
                        var enabled=field=="Vertices"||_request.IncludeNormals;
                        byte[] bytes=first&&enabled?new byte[values.Length*4]:null;
                        for(var i=0;i<values.Length;i++)
                        {
                            if(!SpatialMath.Finite(values[i]) || float.IsInfinity((float)values[i]))throw new BimBridgeException("INVALID_SPATIAL_DATA","Non-finite mesh value.");
                            if(bytes!=null)
                            {
                                var scalar=BitConverter.GetBytes((float)values[i]);if(!BitConverter.IsLittleEndian)Array.Reverse(scalar);
                                System.Buffer.BlockCopy(scalar,0,bytes,i*4,4);
                            }
                        }
                        if(field=="Vertices")
                        {
                            var boundsStarted=Stopwatch.GetTimestamp();
                            try{for(var i=0;i<values.Length;i+=3)bounds.Add(SpatialMath.Transform(instance.Placement,new[]{values[i],values[i+1],values[i+2]}));}
                            finally{_performance?.AddBounds(Stopwatch.GetTimestamp()-boundsStarted);}
                            vertices+=values.Length;
                        }
                        else normals+=values.Length;
                        if(bytes!=null)Buffer(key,field,"<f4",offset,bytes,values.Length);
                        return Task.CompletedTask;
                    },(field,values)=>
                    {
                        var offset=field=="Indices"?indices:edges;
                        var enabled=field=="Indices"?_request.IncludeIndices:_request.IncludeEdgeIndices;
                        byte[] bytes=first&&enabled?new byte[values.Length*4]:null;
                        for(var i=0;i<values.Length;i++)
                        {
                            if(values[i]>=vertices/3)throw new BimBridgeException("INVALID_SPATIAL_DATA","Mesh index is outside the vertices.");
                            if(bytes!=null)
                            {
                                var scalar=BitConverter.GetBytes(values[i]);if(!BitConverter.IsLittleEndian)Array.Reverse(scalar);
                                System.Buffer.BlockCopy(scalar,0,bytes,i*4,4);
                            }
                        }
                        if(field=="Indices")indices+=values.Length;else edges+=values.Length;
                        if(bytes!=null)Buffer(key,field,"<u4",offset,bytes,values.Length);
                        return Task.CompletedTask;
                    },_token).ConfigureAwait(false);
                    if((normals!=0&&normals!=vertices)||indices%3!=0||edges%2!=0)
                        throw new BimBridgeException("INVALID_SPATIAL_DATA","Incomplete mesh vector/index arrays.");
                    if(first)Frame(new {Type="mesh-end",MeshDefinitionKey=key,Vertices=vertices,
                        Normals=_request.IncludeNormals?normals:0,Indices=_request.IncludeIndices?indices:0,EdgeIndices=_request.IncludeEdgeIndices?edges:0});
                    Frame(new {Type="instance",Element=current,MeshDefinitionKey=key,ModelPlacement=instance.Placement});
                    instances++;_instances++;_performance?.Instance();
                };
                if(nodeSnapshot!=null)
                {foreach(var instance in nodeSnapshot.Instances){_token.ThrowIfCancellationRequested();await visitInstance(instance).ConfigureAwait(false);}}
                else await _source.VisitInstancesAsync(current,visitInstance,_token).ConfigureAwait(false);
                if(_request.IncludeDescendants)
                {
                    Func<ElementKey,bool> visitChild=child=>
                    {
                        if(child==null||child.ElementId==Guid.Empty||child.ModelPartId==Guid.Empty)
                            throw new BimBridgeException("INVALID_SPATIAL_DATA","Invalid spatial child identity.");
                        _index.AddNode(child);return true;
                    };
                    if(nodeSnapshot!=null){foreach(var child in nodeSnapshot.Children){_token.ThrowIfCancellationRequested();visitChild(child);}}
                    else await _source.VisitChildrenAsync(current,visitChild,_token).ConfigureAwait(false);
                }
            }
            Frame(new {Type="source-end",Element=root,ProcessedNodes=nodes,InstanceCount=instances,
                BoundsMin=bounds.Min,BoundsMax=bounds.Max,BoundsCenter=bounds.Center,
                State=_failed>failuresBefore?"partial":bounds.Min==null?"no-geometry":"complete"});
        }
        public void Complete()
        {
            Frame(new {Type="summary",ModelVersionUtc=_version,SourceCount=_sources,ProcessedNodes=_nodes,InstanceCount=_instances,MeshCount=_meshes,
                FailedCount=_failed,Complete=_failed==0});_writer.Flush();
        }
        public void Dispose()=>_writer.Dispose();
    }
}
