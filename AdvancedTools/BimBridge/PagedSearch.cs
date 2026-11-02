using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AdvancedTools.BimBridge
{
    public sealed class PagedSearch
    {
        private sealed class CursorState
        {
            public string SelectionId,Query,Version,ModelKey;
            public long Position,Count;
            public int PageSize;
            public DateTime ExpiresAtUtc;
        }
        private readonly ModelContextProvider _models;
        private readonly BimBridgeSettings _settings;
        private readonly string _root,_worker;
        public PagedSearch(ModelContextProvider models,BimBridgeSettings settings,string worker)
        { _models=models;_settings=settings;_worker=worker;_root=Path.Combine(settings.ResultsDirectory,"paging",settings.DurableWorkerId);Directory.CreateDirectory(_root); }
        private static string Hash(string value)
        {using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();}
        private void Save(string name,string text)
        {
            var path=Path.Combine(_root,name);if(File.Exists(path))return;
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {File.WriteAllText(temporary,text);File.Move(temporary,path);}
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        public async Task<object> ExecuteAsync(SearchElementsRequest request,TaskExecutionContext context)
        {
            var size=request.PageSize??10000;
            if(size<=0 || size>_settings.ResourceLimits.MaxSearchResults)
                throw new BimBridgeException("INVALID_REQUEST","PageSize is outside the configured query limit.");
            var fingerprint=Hash(JsonConvert.SerializeObject(new {request.TargetModelKey,request.TargetModelPartIds,request.Conditions,request.LogicalOperator,size}));
            CursorState state;
            if(!string.IsNullOrEmpty(request.Cursor))
            {
                if(request.Cursor.Length!=64 || !request.Cursor.All(Uri.IsHexDigit)) throw new BimBridgeException("INVALID_CURSOR","Malformed cursor.");
                var path=Path.Combine(_root,request.Cursor+".json");
                if(!File.Exists(path)) throw new BimBridgeException("CURSOR_EXPIRED","Cursor is expired or belongs to a different worker.");
                state=JsonConvert.DeserializeObject<CursorState>(File.ReadAllText(path));
                if(state.Query!=fingerprint || state.PageSize!=size) throw new BimBridgeException("INVALID_CURSOR","Cursor belongs to another query.");
                if(state.ExpiresAtUtc<=DateTime.UtcNow) throw new BimBridgeException("CURSOR_EXPIRED","Cursor retention expired.");
                if(request.ModelVersionUtc.HasValue && request.ModelVersionUtc.Value.ToUniversalTime()!=DateTime.Parse(state.Version).ToUniversalTime())
                    throw new BimBridgeException("MODEL_CHANGED","Cursor snapshot differs from the request.");
            }
            else state=null;
            var token=context.CancellationToken;
            using(var model=await _models.GetModelAsync(request.TargetModelKey,request.ModelVersionUtc,token).ConfigureAwait(false))
            {
                context.Resources?.Track(model);
                if(state!=null && model.Version.ToUniversalTime()!=model.InitialLatestVersion.ToUniversalTime())
                    throw new BimBridgeException("MODEL_CHANGED","The model changed after the cursor was created.",true);
                if(state!=null && model.ModelVersionUtc!=state.Version) throw new BimBridgeException("MODEL_CHANGED","Cursor snapshot is no longer current.",true);
                if(state==null)
                {
                    foreach(var cursorPath in Directory.EnumerateFiles(_root,"*.json"))
                    {
                        var expired=JsonConvert.DeserializeObject<CursorState>(File.ReadAllText(cursorPath));
                        if(expired.ExpiresAtUtc<=DateTime.UtcNow)
                        {
                            File.Delete(cursorPath);
                            var selection=Path.Combine(_root,expired.SelectionId+".selection");
                            if(File.Exists(selection))File.Delete(selection);
                        }
                    }
                    var occupied=Directory.EnumerateFiles(_root,"*.selection").Sum(p=>new FileInfo(p).Length);
                    if(occupied>=_settings.SelectionDiskQuotaBytes) throw new BimBridgeException("RESOURCE_LIMIT","Paging selection disk quota reached.");
                    var id=Guid.NewGuid().ToString("N");var path=Path.Combine(_root,id+".selection");var temporary=path+".tmp";
                    long count;
                    try
                    {
                        using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        using(var writer=new BinaryWriter(stream))
                        {
                            count=await new BulkSelectionReader(_models,_settings).VisitAsync(model,new BulkSelectionRequest {
                                ModelPartIds=request.TargetModelPartIds,Conditions=request.Conditions,LogicalOperator=request.LogicalOperator},key=>
                            {
                                if(occupied+stream.Position+32>_settings.SelectionDiskQuotaBytes) throw new BimBridgeException("RESOURCE_LIMIT","Paging selection disk quota reached.");
                                writer.Write(key.ModelPartId.ToByteArray());writer.Write(key.ElementId.ToByteArray());return Task.CompletedTask;
                            },token).ConfigureAwait(false);
                            writer.Flush();stream.Flush(true);
                        }
                        await model.AssertUnchangedAsync(token).ConfigureAwait(false);
                        File.Move(temporary,path);
                    }
                    finally {if(File.Exists(temporary))File.Delete(temporary);}
                    state=new CursorState {SelectionId=id,Query=fingerprint,Version=model.ModelVersionUtc,ModelKey=model.ModelId.ToString("D"),
                        Count=count,Position=0,PageSize=size,ExpiresAtUtc=DateTime.UtcNow.AddMinutes(_settings.CursorRetentionMinutes)};
                }
                var elements=new List<ElementKey>(size);
                using(var stream=File.OpenRead(Path.Combine(_root,state.SelectionId+".selection")))
                using(var reader=new BinaryReader(stream))
                {
                    if(stream.Length!=state.Count*32) throw new BimBridgeException("INTEGRITY_ERROR","Paging selection is damaged.");
                    stream.Position=state.Position*32;
                    for(var i=0;i<size && state.Position+i<state.Count;i++)
                        elements.Add(new ElementKey {ModelPartId=new Guid(reader.ReadBytes(16)),ElementId=new Guid(reader.ReadBytes(16))});
                }
                await model.AssertUnchangedAsync(token).ConfigureAwait(false);
                var hasMore=state.Position+elements.Count<state.Count;
                string next=null;
                if(hasMore)
                {
                    state.Position+=elements.Count;
                    next=Hash(state.SelectionId+":"+state.Position+":"+state.Query);
                    Save(next+".json",JsonConvert.SerializeObject(state));
                }
                else if(string.IsNullOrEmpty(request.Cursor)) File.Delete(Path.Combine(_root,state.SelectionId+".selection"));
                return new {state.ModelKey,ModelVersionUtc=state.Version,Elements=elements,Count=elements.Count,
                    HasMore=hasMore,NextCursor=next,CursorWorkerInstanceId=_worker,Truncated=hasMore};
            }
        }
    }
}
