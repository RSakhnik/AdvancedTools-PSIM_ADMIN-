using Ascon.Pilot.Bim.SDK;
using Ascon.Pilot.Bim.SDK.ModelStorage;
using Newtonsoft.Json;
using Pilot.BimBridge.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
    public sealed class SpatialDataRequest
    {
        public string TargetModelKey { get; set; }
        public DateTime? ModelVersionUtc { get; set; }
        public List<ElementKey> ElementIds { get; set; } = new List<ElementKey>();
        public bool IncludeDescendants { get; set; }
        public bool IncludeMeshes { get; set; }

        public ElementKey[] Validate()
        {
            if (ElementIds == null || ElementIds.Count == 0 || ElementIds.Count > 100 ||
                ElementIds.Any(x => x == null || x.ModelPartId == Guid.Empty || x.ElementId == Guid.Empty))
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest,
                    "ElementIds must contain 1 to 100 non-empty composite identifiers.");
            var seen = new HashSet<string>();
            return ElementIds.Where(x => seen.Add(Key(x))).ToArray();
        }

        internal static string Key(ElementKey key) => key.ModelPartId.ToString("D") + "/" + key.ElementId.ToString("D");
    }

    // Preserve SDK numbers exactly: no physical-unit conversion, origin shift or
    // inferred part transform. Observed SDK placements use last-column translation.
    public sealed class SpatialPlacementAdapter
    {
        public double[] Normalize(double[] sdk)
        {
            SpatialMath.ValidateAffine(sdk);
            return (double[])sdk.Clone();
        }
    }

    // Counts actual UTF-8 output bytes rather than JSON characters. Also protects
    // scratch output; no truncated or oversized file is published as a result.
    public sealed class SpatialLimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private readonly CancellationToken _token;
        private readonly SpatialByteBudget _budget;
        public SpatialLimitedStream(Stream inner, long limit, CancellationToken token, SpatialByteBudget budget=null) { _inner=inner; _limit=limit; _token=token; _budget=budget; }
        public override void Write(byte[] buffer,int offset,int count)
        {
            _token.ThrowIfCancellationRequested();
            if (_limit > 0 && _inner.Position + count > _limit)
                throw new BimBridgeException("RESULT_TOO_LARGE", "Spatial JSON exceeds the byte limit. Reduce ElementIds or disable IncludeMeshes.");
            _budget?.Reserve(count);
            _inner.Write(buffer,offset,count);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] b,int o,int c) => throw new NotSupportedException();
        public override long Seek(long o,SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    public sealed class SpatialByteBudget
    {
        private readonly long _limit;
        private long _used;
        public SpatialByteBudget(long limit) { _limit=limit; }
        public void Reserve(int bytes)
        {
            if(Interlocked.Add(ref _used,bytes)>_limit)
                throw new BimBridgeException("RESULT_TOO_LARGE","Spatial scratch output exceeds the byte limit. Reduce the batch or disable meshes.");
        }
    }

    internal sealed class SpatialScratch : IDisposable
    {
        private readonly List<string> _files=new List<string>();
        private readonly string _directory;
        public SpatialScratch(string directory) { _directory=directory; Directory.CreateDirectory(directory); }
        public string NewFile() { var path=Path.Combine(_directory,Guid.NewGuid().ToString("N")+".spatial.tmp"); _files.Add(path);return path; }
        public void Dispose() { foreach(var path in _files) TemporaryFileCleaner.TryDelete(path); }
    }

    public sealed class SpatialCollectionSummary
    {
        public int ProcessedNodes {get;set;}
        public int FailedSources {get;set;}
        public bool Partial {get;set;}
    }

    public sealed class SpatialCollector
    {
        private readonly ISpatialSource _source;
        private readonly SpatialPlacementAdapter _profile;
        private readonly Func<Guid,bool> _partAllowed;
        public SpatialCollector(ISpatialSource source,SpatialPlacementAdapter profile,Func<Guid,bool> partAllowed)
        { _source=source;_profile=profile;_partAllowed=partAllowed; }

        public async Task<SpatialCollectionSummary> WriteAsync(JsonTextWriter writer,SpatialDataRequest request,
            ResourceLimitSettings limits,string scratchDirectory,Action<int,string> progress,CancellationToken token)
        {
            var roots=request.Validate();
            var serializer=JsonSerializer.CreateDefault();
            var summary=new SpatialCollectionSummary();
            var errors=new List<ElementReadError>();
            var failed=new List<ElementKey>();
            var definitions=new Dictionary<string,string>();
            var visitedJob=new HashSet<string>();
            // Shared child cache is bounded; large adjacency lists are deliberately not cached.
            var childCache=new Dictionary<string,ElementKey[]>();
            var scratchBudget=new SpatialByteBudget(limits.MaxSpatialResultBytes);
            using(var scratch=new SpatialScratch(scratchDirectory))
            {
                writer.WritePropertyName("Elements");writer.WriteStartArray();
                foreach(var root in roots)
                {
                    token.ThrowIfCancellationRequested();
                    var bounds=new SpatialBounds();
                    var localSeen=new HashSet<string>();
                    var queue=new Queue<ElementKey>();queue.Enqueue(root);
                    var scheduled=new HashSet<string> {SpatialDataRequest.Key(root)};
                    var state="complete";var geometryNodes=0;var instanceCount=0;var nodeCount=0;
                    var instancePath=scratch.NewFile();
                    using(var stream=new FileStream(instancePath,FileMode.CreateNew,FileAccess.Write))
                    using(var limited=new SpatialLimitedStream(stream,limits.MaxSpatialResultBytes,token,scratchBudget))
                    using(var text=new StreamWriter(limited))
                    {
                        while(queue.Count>0)
                        {
                            token.ThrowIfCancellationRequested();
                            var key=queue.Dequeue();var nodeKey=SpatialDataRequest.Key(key);
                            if(!localSeen.Add(nodeKey)) continue;
                            if(!visitedJob.Contains(nodeKey) && visitedJob.Count>=limits.MaxSpatialTraversalNodes)
                            { state="partial"; AddError(key,"SPATIAL_TRAVERSAL_LIMIT","Traversal node limit reached.",errors); break; }
                            visitedJob.Add(nodeKey);nodeCount++;
                            progress?.Invoke(Math.Min(95,visitedJob.Count*95/limits.MaxSpatialTraversalNodes),"Геометрия части "+key.ModelPartId+": узлов "+visitedJob.Count);
                            try
                            {
                                if(!_partAllowed(key.ModelPartId)) throw new BimBridgeException("PART_NOT_AVAILABLE","Part is unavailable or outside model scope.",true);
                                if(!await _source.ExistsAsync(key,token).ConfigureAwait(false)) throw new BimBridgeException("ELEMENT_NOT_FOUND","Element is absent from the selected snapshot.");
                                var nodeHasGeometry=false;
                                await _source.VisitInstancesAsync(key,async instance =>
                                {
                                    token.ThrowIfCancellationRequested();
                                    if(!_partAllowed(instance.MeshPartId)||instance.MeshId==Guid.Empty) throw new BimBridgeException("INVALID_SPATIAL_DATA","Invalid mesh identity.");
                                    var matrix=_profile.Normalize(instance.Placement);
                                    var definitionKey=instance.MeshPartId.ToString("D")+"/"+instance.MeshId.ToString("D");
                                    var definitionPath=request.IncludeMeshes&&!definitions.ContainsKey(definitionKey)?scratch.NewFile():null;
                                    try
                                    {
                                        var meshBounds=await ReadMeshAsync(instance,matrix,definitionKey,definitionPath,limits.MaxSpatialResultBytes,scratchBudget,token).ConfigureAwait(false);
                                        if(definitionPath!=null) definitions.Add(definitionKey,definitionPath);
                                        bounds.Union(meshBounds);
                                        if(meshBounds.Min!=null && !nodeHasGeometry) {nodeHasGeometry=true;geometryNodes++;}
                                        text.WriteLine(JsonConvert.SerializeObject(new {Element=key,MeshDefinitionKey=definitionKey,ModelPlacement=matrix}));
                                        instanceCount++;
                                    }
                                    catch { if(definitionPath!=null) TemporaryFileCleaner.TryDelete(definitionPath);throw; }
                                },token).ConfigureAwait(false);
                                if(request.IncludeDescendants)
                                {
                                    ElementKey[] cached;
                                    if(childCache.TryGetValue(nodeKey,out cached))
                                    {
                                        foreach(var child in cached)
                                        {
                                            var childKey=SpatialDataRequest.Key(child);
                                            if(scheduled.Contains(childKey)) continue;
                                            if(scheduled.Count>=limits.MaxSpatialTraversalNodes) {state="partial";AddError(key,"SPATIAL_TRAVERSAL_LIMIT","Traversal node limit reached.",errors);break;}
                                            scheduled.Add(childKey);queue.Enqueue(child);
                                        }
                                    }
                                    if(!childCache.ContainsKey(nodeKey))
                                    {
                                        var children=new List<ElementKey>();var complete=true;
                                        await _source.VisitChildrenAsync(key,child =>
                                        {
                                            if(child==null||child.ModelPartId==Guid.Empty||child.ElementId==Guid.Empty) throw new BimBridgeException("INVALID_SPATIAL_DATA","Invalid child identity.");
                                            if(children.Count<=256) children.Add(child);
                                            var childKey=SpatialDataRequest.Key(child);
                                            if(scheduled.Contains(childKey)) return true;
                                            if(scheduled.Count>=limits.MaxSpatialTraversalNodes) { complete=false;return false; }
                                            scheduled.Add(childKey);queue.Enqueue(child);return true;
                                        },token).ConfigureAwait(false);
                                        if(!complete) {state="partial";AddError(key,"SPATIAL_TRAVERSAL_LIMIT","Child enumeration exceeds traversal limit.",errors);}
                                        if(complete&&children.Count<=256) {if(childCache.Count>=128) childCache.Clear();childCache[nodeKey]=children.ToArray();}
                                    }
                                }
                            }
                            catch(OperationCanceledException) { throw; }
                            catch(BimBridgeException error) when(error.Code=="ELEMENT_NOT_FOUND" || error.Code=="PART_NOT_AVAILABLE" || error.Code=="INVALID_SPATIAL_DATA" || error.Code=="GEOMETRY_READ_FAILED")
                            { state=SpatialDataRequest.Key(root)==nodeKey?"failed":"partial";AddError(key,error.Code,error.Message,errors); }
                            catch(Exception error) when(!(error is BimBridgeException))
                            { state=SpatialDataRequest.Key(root)==nodeKey?"failed":"partial";AddError(key,"ELEMENT_READ_FAILED",error.GetType().Name,errors); }
                        }
                    }
                    if(state=="complete" && bounds.Min==null) state="no-geometry";
                    if(state=="failed"||state=="partial") { failed.Add(root);summary.FailedSources++;summary.Partial=true; }
                    writer.WriteStartObject();
                    Property(writer,serializer,"Element",root);
                    Property(writer,serializer,"Scope",request.IncludeDescendants?"subtree":"self");
                    Property(writer,serializer,"State",state);
                    Property(writer,serializer,"GeometryElementCount",geometryNodes);
                    Property(writer,serializer,"InstanceCount",instanceCount);
                    Property(writer,serializer,"ProcessedNodes",nodeCount);
                    Property(writer,serializer,"BoundsMin",bounds.Min);
                    Property(writer,serializer,"BoundsMax",bounds.Max);
                    Property(writer,serializer,"BoundsCenter",bounds.Center);
                    writer.WritePropertyName("Instances");writer.WriteStartArray();
                    using(var reader=new StreamReader(instancePath))
                    { string line;while((line=reader.ReadLine())!=null) {token.ThrowIfCancellationRequested();writer.WriteRawValue(line);} }
                    writer.WriteEndArray();writer.WriteEndObject();
                }
                writer.WriteEndArray();
                if(request.IncludeMeshes)
                {
                    writer.WritePropertyName("MeshDefinitions");writer.WriteStartArray();
                    foreach(var file in definitions.Values)
                    {
                        token.ThrowIfCancellationRequested();
                        using(var text=new StreamReader(file)) using(var json=new JsonTextReader(text))
                            while(json.Read()) {token.ThrowIfCancellationRequested();writer.WriteToken(json,false);}
                    }
                    writer.WriteEndArray();
                }
                Property(writer,serializer,"FailedElementIds",failed);
                Property(writer,serializer,"ElementErrors",errors);
                Property(writer,serializer,"Warnings",summary.Partial?new[]{"Some requested spatial data is incomplete. Inspect State and ElementErrors."}:new string[0]);
                Property(writer,serializer,"Truncated",summary.Partial);
                summary.ProcessedNodes=visitedJob.Count;
                return summary;
            }
        }

        private async Task<SpatialBounds> ReadMeshAsync(SpatialInstance instance,double[] matrix,string key,string path,long limit,SpatialByteBudget budget,CancellationToken token)
        {
            var bounds=new SpatialBounds();long vertices=0,normals=0,triangles=0,edges=0;
            string current=null;
            using(var file=path==null?null:new FileStream(path,FileMode.CreateNew,FileAccess.Write))
            using(var limited=file==null?null:new SpatialLimitedStream(file,limit,token,budget))
            using(var text=limited==null?null:new StreamWriter(limited))
            using(var writer=text==null?null:new JsonTextWriter(text))
            {
                if(writer!=null) {writer.WriteStartObject();writer.WritePropertyName("Key");writer.WriteValue(key);}
                Action<string> start=field=> {if(current==field) return;if(writer!=null) {if(current!=null) writer.WriteEndArray();writer.WritePropertyName(field);writer.WriteStartArray();} current=field;};
                await _source.VisitMeshAsync(instance,(field,batch)=>
                {
                    start(field);token.ThrowIfCancellationRequested();
                    if(batch.Length%3!=0 || batch.Any(x=>!SpatialMath.Finite(x))) throw new BimBridgeException("INVALID_SPATIAL_DATA","Invalid vertex or normal triples.");
                    if(field=="Vertices")
                    {
                        vertices+=batch.Length/3;
                        for(var i=0;i<batch.Length;i+=3)
                        {
                            var p=new[]{batch[i],batch[i+1],batch[i+2]};
                            bounds.Add(SpatialMath.Transform(matrix,p));
                            if(writer!=null) foreach(var value in p) writer.WriteValue(value);
                        }
                    }
                    else {normals+=batch.Length/3;if(writer!=null) foreach(var value in batch) writer.WriteValue(value);}
                    return Task.CompletedTask;
                },(field,batch)=>
                {
                    start(field);token.ThrowIfCancellationRequested();
                    if(batch.Any(x=>x>=vertices)) throw new BimBridgeException("INVALID_SPATIAL_DATA","Mesh index is outside the vertex array.");
                    if(field=="Indices") triangles+=batch.Length;else edges+=batch.Length;
                    if(writer!=null) foreach(var value in batch) writer.WriteValue(value);
                    return Task.CompletedTask;
                },token).ConfigureAwait(false);
                if((normals!=0&&normals!=vertices)||triangles%3!=0||edges%2!=0)
                    throw new BimBridgeException("INVALID_SPATIAL_DATA","Incomplete mesh normals, triangles or edges.");
                if(writer!=null) {if(current!=null) writer.WriteEndArray();writer.WriteEndObject();writer.Flush();}
            }
            return bounds;
        }

        private static void AddError(ElementKey key,string code,string message,List<ElementReadError> errors)
        {
            // Keep diagnostics bounded even for a large damaged subtree.
            if(errors.Count<1000) errors.Add(new ElementReadError {ModelPartId=key.ModelPartId,ElementId=key.ElementId,Code=code,Message=message});
        }
        internal static void Property(JsonWriter writer,JsonSerializer serializer,string name,object value)
        {writer.WritePropertyName(name);serializer.Serialize(writer,value);}
    }

    public sealed class GetElementSpatialDataOperationHandler : IBimOperationHandler,IModelKeyResolverHandler
    {
        private readonly ModelContextProvider _models;
        private readonly BimBridgeSettings _settings;
        private readonly ResultFileWriter _writer;
        public GetElementSpatialDataOperationHandler(ModelContextProvider models,BimBridgeSettings settings,ResultFileWriter writer)
        {_models=models;_settings=settings;_writer=writer;}
        public IReadOnlyCollection<string> OperationCodes {get;}=new[]{"get-element-spatial-data"};
        public string Capability=>"element-spatial-data";
        public bool IsAvailable=>_models.HasModelServices;
        public async Task<object> ExecuteAsync(TaskExecutionContext context)
        {
            var request=BimOperationDispatcher.ReadPayload<SpatialDataRequest>(context.Assignment);
            var keys=request.Validate();
            if(string.IsNullOrWhiteSpace(request.TargetModelKey)) request.TargetModelKey=context.Assignment.TargetModelKey;
            var profile=new SpatialPlacementAdapter();
            var token=context.CancellationToken;
            var model=await _models.GetModelAsync(request.TargetModelKey,request.ModelVersionUtc,token).ConfigureAwait(false);
            context.Resources?.Track(model);
            using(model)
            {
                await _models.ResolveModelPartsAsync(model.ModelId,keys.Select(x=>x.ModelPartId),false,token).ConfigureAwait(false);
                var available=new HashSet<Guid>(await model.Sdk.ReadAsync(s=>(s.GetModelPartsIds()??Enumerable.Empty<Guid>()).ToArray(),token).ConfigureAwait(false));
                var collector=new SpatialCollector(new SpatialSdkSource(model),profile,p=>available.Contains(p)&&_models.IsPartInScope(model.ModelId,model.TargetModelPartIds,p));
                var result=await _writer.WriteStreamingJsonAsync(context.Assignment.TaskId,async json=>
                {
                    var serializer=JsonSerializer.CreateDefault();json.WriteStartObject();
                    SpatialCollector.Property(json,serializer,"ModelKey",model.ModelId);
                    SpatialCollector.Property(json,serializer,"ModelVersionUtc",model.ModelVersionUtc);
                    SpatialCollector.Property(json,serializer,"CoordinateSpace","sdk-placement");
                    SpatialCollector.Property(json,serializer,"SceneCoordinatesVerified",false);
                    SpatialCollector.Property(json,serializer,"MatrixConvention",new {Storage="row-major",Vector="column",VertexSpace="mesh-local",NormalsSpace="mesh-local"});
                    var summary=await collector.WriteAsync(json,request,_settings.ResourceLimits,_writer.TemporaryDirectory,context.ReportProgress,token).ConfigureAwait(false);
                    await model.AssertUnchangedAsync(token).ConfigureAwait(false);
                    json.WriteEndObject();
                    System.Diagnostics.Trace.TraceInformation("BIM spatial model={0} version={1} sources={2} nodes={3} failed={4}",model.ModelId,model.ModelVersionUtc,keys.Length,summary.ProcessedNodes,summary.FailedSources);
                    context.ReportProgress?.Invoke(99,"Подготовка пространственного результата");
                },token,_settings.ResourceLimits.MaxSpatialResultBytes).ConfigureAwait(false);
                try {await model.AssertUnchangedAsync(token).ConfigureAwait(false);return result;}
                catch {result.Dispose();throw;}
            }
        }
    }

    // Raw, bounded samples only. It never labels unverified values as metres/model coordinates.
    public sealed class DiagnoseElementSpatialDataOperationHandler : IBimOperationHandler,IModelKeyResolverHandler
    {
        private readonly ModelContextProvider _models;
        private readonly BimBridgeSettings _settings;
        private readonly ResultFileWriter _writer;
        public DiagnoseElementSpatialDataOperationHandler(ModelContextProvider models,BimBridgeSettings settings,ResultFileWriter writer)
        {_models=models;_settings=settings;_writer=writer;}
        public IReadOnlyCollection<string> OperationCodes {get;}=new[]{"diagnose-element-spatial-data"};
        public string Capability=>"spatial-diagnostics";
        public bool IsAvailable=>_models.HasModelServices&&_settings.DiagnosticOperationsEnabled;
        public async Task<object> ExecuteAsync(TaskExecutionContext context)
        {
            var request=BimOperationDispatcher.ReadPayload<SpatialDataRequest>(context.Assignment);
            var keys=request.Validate();
            if(keys.Length>5) throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest,"Diagnostics accepts at most five elements.");
            var token=context.CancellationToken;
            var model=await _models.GetModelAsync(request.TargetModelKey??context.Assignment.TargetModelKey,request.ModelVersionUtc,token).ConfigureAwait(false);
            context.Resources?.Track(model);
            using(model)
            {
                await _models.ResolveModelPartsAsync(model.ModelId,keys.Select(x=>x.ModelPartId),false,token).ConfigureAwait(false);
                var samples=new List<object>();
                foreach(var key in keys)
                {
                    if(!_models.IsPartInScope(model.ModelId,model.TargetModelPartIds,key.ModelPartId)||!await model.HasPartAsync(key.ModelPartId,token).ConfigureAwait(false))
                        throw new BimBridgeException("PART_NOT_AVAILABLE","Diagnostic element is outside the available model scope.");
                    var source=new SpatialSdkSource(model);
                    if(!await source.ExistsAsync(key,token).ConfigureAwait(false)) throw new BimBridgeException("ELEMENT_NOT_FOUND","Diagnostic element not found.");
                    // Deliberately copy only the first instance and first eight vertices on the Dispatcher.
                    var sample=await model.Sdk.ReadAsync(s=>
                    {
                        using(var instances=(s.LoadElementMeshInstances(new ModelElementId(key.ModelPartId,key.ElementId),model.Version)??Enumerable.Empty<IMeshInstance>()).GetEnumerator())
                        {
                            if(!instances.MoveNext()) return (object)new {Element=key,HasOwnGeometry=false};
                            var instance=instances.Current;
                            var mesh=s.LoadMeshDefinition(instance.MeshDefinitionId);
                            return new {Element=key,HasOwnGeometry=true,MeshPartId=instance.MeshDefinitionId.ModelPartId,
                                MeshId=instance.MeshDefinitionId.MeshId,Placement=SpatialMath.Matrix(instance.Placement),
                                RawVertices=mesh?.Vertices?.Take(24).ToArray()};
                        }
                    },token).ConfigureAwait(false);
                    samples.Add(sample);
                }
                var assembly=typeof(IModelStorage).Assembly;
                string hash;using(var file=File.OpenRead(assembly.Location)) using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
                return await SnapshotResultPublisher.WriteAsync(_writer,model,context.Assignment.TaskId,json=>
                {
                    JsonSerializer.CreateDefault().Serialize(json,new {ModelKey=model.ModelId,ModelVersionUtc=model.ModelVersionUtc,
                        CoordinateSpace="sdk-raw",SdkVersion=assembly.GetName().Version.ToString(),SdkSha256=hash,Samples=samples});
                    return Task.CompletedTask;
                },token).ConfigureAwait(false);
            }
        }
    }
}
