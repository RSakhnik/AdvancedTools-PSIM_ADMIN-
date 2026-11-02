using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Pilot.BimBridge.Contracts;

namespace AdvancedTools.BimBridge
{
    public sealed class ExportElementsRequest:BulkSelectionRequest
    {
        public List<string> PropertyNames {get;set;}
        public bool IncludeIdentifiers {get;set;}=true;
        public bool Gzip {get;set;}=true;
    }
    public sealed class ExportElementsOperationHandler:IBimOperationHandler,IModelKeyResolverHandler
    {
        private readonly ModelContextProvider _models;private readonly BimBridgeSettings _settings;private readonly ArtifactFileWriter _writer;
        public ExportElementsOperationHandler(ModelContextProvider models,BimBridgeSettings settings,ArtifactFileWriter writer)
        {_models=models;_settings=settings;_writer=writer;}
        public IReadOnlyCollection<string> OperationCodes {get;}=new[]{"export-elements"};
        public string Capability=>ArtifactProtocol.ExportElements;
        public bool IsAvailable=>_models.HasModelServices;
        public async Task<object> ExecuteAsync(TaskExecutionContext context)
        {
            var request=BimOperationDispatcher.ReadPayload<ExportElementsRequest>(context.Assignment);
            request.TargetModelKey=request.TargetModelKey??context.Assignment.TargetModelKey;
            var token=context.CancellationToken;
            var model=await _models.GetModelAsync(request.TargetModelKey,request.ModelVersionUtc,token).ConfigureAwait(false);
            context.Resources?.Track(model);
            using(model)
            {
                var names=request.PropertyNames==null?null:new HashSet<string>(request.PropertyNames,StringComparer.OrdinalIgnoreCase);
                var result=await _writer.WriteAsync(context.Assignment.TaskId,"application/x-ndjson","elements.ndjson",request.Gzip,
                    async (stream,cancellation)=>
                    {
                        using(var text=new StreamWriter(stream,new UTF8Encoding(false),81920,true))
                        {
                            await text.WriteLineAsync(JsonConvert.SerializeObject(new {Type="manifest",Format="bim-elements",FormatVersion=1,
                                ModelKey=model.ModelId,ModelVersionUtc=model.ModelVersionUtc,request.IncludeIdentifiers,request.PropertyNames})).ConfigureAwait(false);
                            long exported=0,failed=0;
                            var count=await new BulkSelectionReader(_models,_settings).VisitAsync(model,request,async key=>
                            {
                                object record;
                                try
                                {
                                    var properties=await model.Sdk.ReadAsync(storage=>SnapshotElementReader.ReadProperties(storage,key,model.Version,names,cancellation),cancellation).ConfigureAwait(false);
                                    if(!properties.Exists) throw new BimBridgeException("ELEMENT_NOT_FOUND","Element is absent from the pinned snapshot.");
                                    var element=new Dictionary<string,object> { ["Type"]="element",["PropertySets"]=properties.PropertySets };
                                    if(request.IncludeIdentifiers) {element["ModelPartId"]=key.ModelPartId;element["ElementId"]=key.ElementId;}
                                    record=element;exported++;
                                }
                                catch(BimBridgeException error) when(error.Code=="ELEMENT_NOT_FOUND" || error.Code=="ELEMENT_READ_FAILED")
                                {record=new {Type="error",key.ModelPartId,key.ElementId,error.Code,error.Message};failed++;}
                                var line=JsonConvert.SerializeObject(record);
                                if(Encoding.UTF8.GetByteCount(line)>_settings.MaxExportRecordBytes)
                                    throw new BimBridgeException("RESULT_TOO_LARGE","One element exceeds the NDJSON record byte limit.");
                                await text.WriteLineAsync(line).ConfigureAwait(false);
                                if((exported+failed)%1000==0) context.ReportProgress?.Invoke(0,"Экспортировано элементов: "+exported+"; ошибок: "+failed);
                            },cancellation).ConfigureAwait(false);
                            await model.AssertUnchangedAsync(cancellation).ConfigureAwait(false);
                            await text.WriteLineAsync(JsonConvert.SerializeObject(new {Type="summary",SelectedCount=count,ExportedCount=exported,FailedCount=failed,
                                Complete=failed==0,ModelVersionUtc=model.ModelVersionUtc})).ConfigureAwait(false);
                            await text.FlushAsync().ConfigureAwait(false);
                        }
                    },token).ConfigureAwait(false);
                try {await model.AssertUnchangedAsync(token).ConfigureAwait(false);return result;}
                catch {result.Dispose();throw;}
            }
        }
    }
}
