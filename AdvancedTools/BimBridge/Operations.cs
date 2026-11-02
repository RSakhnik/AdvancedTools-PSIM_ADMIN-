using Ascon.Pilot.Bim.SDK;
using Ascon.Pilot.Bim.SDK.Search;
using Ascon.Pilot.Bim.Search.SDK.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pilot.BimBridge.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
    public sealed class TaskExecutionContext
    {
        public BimTaskAssignmentDto Assignment { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Action<int, string> ReportProgress { get; set; }
        public OperationResourceScope Resources { get; set; }
    }

    public interface IBimOperationHandler
    {
        IReadOnlyCollection<string> OperationCodes { get; }
        string Capability { get; }
        bool IsAvailable { get; }
        Task<object> ExecuteAsync(TaskExecutionContext context);
    }

    public interface IModelKeyResolverHandler
    {
    }

    public interface IAdditionalCapabilities
    {
        IEnumerable<string> AdditionalCapabilities { get; }
    }

    public sealed class BimOperationDispatcher
    {
        private readonly Dictionary<string, IBimOperationHandler> _handlers;

        public BimOperationDispatcher(IEnumerable<IBimOperationHandler> handlers)
        {
            _handlers = new Dictionary<string, IBimOperationHandler>(StringComparer.OrdinalIgnoreCase);
            foreach (var handler in handlers.Where(x => x.IsAvailable))
                foreach (var code in handler.OperationCodes)
                    _handlers[code] = handler;
        }

        public string[] Capabilities
        {
            get
            {
                var capabilities = _handlers.Values.Select(x => x.Capability)
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                capabilities = capabilities.Concat(_handlers.Values.OfType<IAdditionalCapabilities>().SelectMany(x => x.AdditionalCapabilities));
                if (_handlers.Values.Any(x => x is IModelKeyResolverHandler))
                {
                    capabilities = capabilities.Concat(new[] { "resolve-model-key" });
                }
                return capabilities
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToArray();
            }
        }

        public string[] OperationCodes => _handlers.Keys.OrderBy(x => x).ToArray();

        public Task<object> ExecuteAsync(TaskExecutionContext context)
        {
            if (context?.Assignment == null)
                throw new ArgumentNullException(nameof(context));
            if (!_handlers.TryGetValue(context.Assignment.OperationCode ?? string.Empty, out var handler))
                throw new BimBridgeException("CAPABILITY_NOT_AVAILABLE",
                    $"Operation '{context.Assignment.OperationCode}' is not available.");
            return handler.ExecuteAsync(context);
        }

        public static T ReadPayload<T>(BimTaskAssignmentDto assignment) where T : class, new()
        {
            var bytes = assignment?.Payload ?? Array.Empty<byte>();
            if (bytes.Length == 0) return new T();
            if (string.Equals(assignment.PayloadEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                using (var input = new MemoryStream(bytes))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    gzip.CopyTo(output);
                    bytes = output.ToArray();
                }
            }
            var json = Encoding.UTF8.GetString(bytes);
            return JsonConvert.DeserializeObject<T>(json) ?? new T();
        }
    }

    public sealed class ModelContextOperationHandler : IBimOperationHandler, IModelKeyResolverHandler
    {
        private readonly ModelContextProvider _models;

        public ModelContextOperationHandler(ModelContextProvider models) { _models = models; }
        public IReadOnlyCollection<string> OperationCodes { get; } = new[] { "get-model-context", "model-context" };
        public string Capability => "model-context";
        public bool IsAvailable => _models.HasModelServices;

        public async Task<object> ExecuteAsync(TaskExecutionContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var readiness = await _models.WaitForPartsCatalogAsync(
                TimeSpan.FromSeconds(30),
                context.CancellationToken).ConfigureAwait(false);
            var models = await _models.GetModelsAsync(context.CancellationToken).ConfigureAwait(false);
            return new
            {
                GeneratedAtUtc = DateTime.UtcNow,
                ActiveModelKey = models.FirstOrDefault(x => x.IsActive)?.ModelKey,
                CatalogState = readiness.State,
                IsPartsCatalogComplete = readiness.IsComplete,
                UnresolvedPartCount = readiness.UnresolvedPartCount,
                CatalogWarnings = readiness.Warnings,
                Projects = _models.GetProjects(),
                Models = models
            };
        }
    }

    public sealed class SearchElementsOperationHandler : IBimOperationHandler, IModelKeyResolverHandler, IAdditionalCapabilities
    {
        private readonly ModelContextProvider _models;
        private readonly IModelSearchManager _searchManager;
        private readonly BimBridgeSettings _settings;
        private readonly ResultFileWriter _resultWriter;
        private readonly PagedSearch _paging;
        public IEnumerable<string> AdditionalCapabilities => _paging == null ? Array.Empty<string>() : new[] { ArtifactProtocol.PagedSearch };

        public SearchElementsOperationHandler(ModelContextProvider models, IModelSearchManager searchManager)
            : this(models, searchManager, new BimBridgeSettings())
        {
        }

        public SearchElementsOperationHandler(
            ModelContextProvider models,
            IModelSearchManager searchManager,
            BimBridgeSettings settings,
            ResultFileWriter resultWriter = null, PagedSearch paging = null)
        {
            _models = models;
            _searchManager = searchManager;
            _settings = settings ?? new BimBridgeSettings();
            _resultWriter = resultWriter ?? new ResultFileWriter(_settings);
            _paging = paging;
        }

        public IReadOnlyCollection<string> OperationCodes { get; } = new[] { "search-elements" };
        public string Capability => "search-elements";
        public bool IsAvailable => _models.HasModelServices && _searchManager != null;

        public async Task<object> ExecuteAsync(TaskExecutionContext context)
        {
            var request = BimOperationDispatcher.ReadPayload<SearchElementsRequest>(context.Assignment);
            request.TargetModelKey = string.IsNullOrWhiteSpace(request.TargetModelKey)
                ? context.Assignment.TargetModelKey
                : request.TargetModelKey;
            if (request.PageSize.HasValue || request.Cursor != null)
            {
                if (_paging == null) throw new BimBridgeException("CAPABILITY_NOT_AVAILABLE", "Paged search is unavailable.");
                return await _paging.ExecuteAsync(request, context).ConfigureAwait(false);
            }
            var model = await _models.GetSearchTargetAsync(
                request.TargetModelKey,
                context.CancellationToken).ConfigureAwait(false);
            if (request.Conditions == null || request.Conditions.Count == 0)
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "At least one search condition is required.");
            var max = request.MaxResults <= 0
                ? Math.Min(10000, _settings.ResourceLimits.MaxSearchResults)
                : Math.Min(request.MaxResults, _settings.ResourceLimits.MaxSearchResults);
            context.ReportProgress?.Invoke(10, "РџРѕРґРіРѕС‚РѕРІРєР° РїРѕРёСЃРєР°");

            var state = BuildSearchState(request);
            var requestedParts = request.TargetModelPartIds?
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray() ?? Array.Empty<Guid>();
            if (model.TargetModelPartIds.Count > 0 &&
                requestedParts.Length > 0 &&
                requestedParts.Any(x => !model.TargetModelPartIds.Contains(x)))
            {
                throw new BimBridgeException(
                    BimBridgeErrorCodes.InvalidRequest,
                    "TargetModelPartIds contains a part outside the TargetModelKey scope.");
            }
            var effectiveParts = requestedParts.Length > 0
                ? requestedParts
                : (model.TargetModelPartIds.Count > 0
                    ? model.TargetModelPartIds.ToArray()
                    : _models.GetKnownModelPartIds(model.ModelId));
            var isWholeModelSearch = requestedParts.Length == 0 && model.TargetModelPartIds.Count == 0;
            if (isWholeModelSearch && !_models.GetCatalogReadiness().IsComplete)
                throw new BimBridgeException(
                    BimBridgeErrorCodes.ModelNotAvailable,
                    "The model parts catalog is partial; whole-model search cannot guarantee complete results.",
                    true);
            if (effectiveParts.Length == 0)
                throw new BimBridgeException(
                    BimBridgeErrorCodes.ModelNotAvailable,
                    "The model parts catalog is not ready for bounded whole-model search.",
                    true);

            using (var snapshot = await _models.GetModelAsync(request.TargetModelKey, request.ModelVersionUtc, context.CancellationToken).ConfigureAwait(false))
            {
            context.Resources?.Track(snapshot);
            ModelSnapshotPolicy.AssertSearchVersion(snapshot.Version, snapshot.InitialLatestVersion);
            foreach (var part in effectiveParts)
                if (!await snapshot.HasPartAsync(part, context.CancellationToken).ConfigureAwait(false))
                    throw new BimBridgeException("PART_NOT_AVAILABLE", "Requested part is unavailable in storage.", true);
            return await SnapshotResultPublisher.WriteAsync(_resultWriter, snapshot,
                context.Assignment.TaskId,
                async writer =>
                {
                    var count = 0;
                    var truncated = false;
                    writer.WriteStartObject();
                    writer.WritePropertyName("ModelKey");
                    writer.WriteValue(model.ModelId.ToString("D"));
                    writer.WritePropertyName("ModelVersionUtc");
                    writer.WriteValue(snapshot.ModelVersionUtc);
                    writer.WritePropertyName("SearchConsistency");
                    writer.WriteValue("element-existence-validated");
                    writer.WritePropertyName("TargetModelPartIds");
                    writer.WriteStartArray();
                    foreach (var partId in effectiveParts) writer.WriteValue(partId);
                    writer.WriteEndArray();
                    writer.WritePropertyName("Elements");
                    writer.WriteStartArray();

                    for (var partIndex = 0; partIndex < effectiveParts.Length; partIndex++)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        var lease = await _models.GetSearchServiceAsync(
                            model,
                            effectiveParts[partIndex],
                            context.CancellationToken).ConfigureAwait(false);
                        context.Resources?.Track(lease);
                        using (lease)
                        {
                            var enumerator = await snapshot.Sdk.ReadAsync(storage => lease.Service.Search(
                                state, Math.Max(1, max - count + 1)).GetEnumerator(), CancellationToken.None).ConfigureAwait(false);
                            try
                            {
                            while (true)
                            {
                                var item = await snapshot.Sdk.ReadAsync(storage =>
                                {
                                    if (!enumerator.MoveNext()) return null;
                                    var current = enumerator.Current;
                                    return new ElementKey { ModelPartId = current.ModelPartId, ElementId = current.ElementId };
                                }, context.CancellationToken).ConfigureAwait(false);
                                if (item == null) break;
                                context.CancellationToken.ThrowIfCancellationRequested();
                                if (count >= max)
                                {
                                    truncated = true;
                                    break;
                                }
                                await snapshot.Sdk.ReadAsync(storage =>
                                {
                                    SnapshotElementReader.AssertSearchElement(storage, item, snapshot.Version);
                                    return true;
                                }, context.CancellationToken).ConfigureAwait(false);
                                writer.WriteStartObject();
                                writer.WritePropertyName("ModelPartId");
                                writer.WriteValue(item.ModelPartId);
                                writer.WritePropertyName("ElementId");
                                writer.WriteValue(item.ElementId);
                                writer.WriteEndObject();
                                count++;
                            }
                            }
                            finally
                            {
                                await snapshot.Sdk.ReadAsync(storage => { enumerator.Dispose(); return true; }, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        context.ReportProgress?.Invoke(
                            Math.Min(99, (partIndex + 1) * 100 / effectiveParts.Length),
                            "РџРѕРёСЃРє РїРѕ С‡Р°СЃС‚СЏРј РјРѕРґРµР»Рё");
                        if (truncated) break;
                    }

                    writer.WriteEndArray();
                    writer.WritePropertyName("Count");
                    writer.WriteValue(count);
                    writer.WritePropertyName("Truncated");
                    writer.WriteValue(truncated);
                    await snapshot.AssertUnchangedAsync(context.CancellationToken).ConfigureAwait(false);
                    System.Diagnostics.Trace.TraceInformation("BIM search model={0} version={1} found={2}", snapshot.ModelId, snapshot.ModelVersionUtc, count);
                    _models.LogReadSummary("search-elements", snapshot, effectiveParts, count, 0);
                    writer.WriteEndObject();
                    context.ReportProgress?.Invoke(100, "РџРѕРёСЃРє Р·Р°РІРµСЂС€РµРЅ");
                },
                context.CancellationToken).ConfigureAwait(false);
            }
        }

        internal static SearchSetState BuildSearchState(SearchElementsRequest request) => SearchFilters.Build(request);
    }

    public sealed class LoadPropertiesRequest
    {
        public string TargetModelKey { get; set; }
        public DateTime? ModelVersionUtc { get; set; }
        public List<ElementKey> ElementIds { get; set; } = new List<ElementKey>();
        public List<string> PropertyNames { get; set; }
        public int BatchSize { get; set; } = 100;
    }

    public sealed class LoadElementPropertiesOperationHandler : IBimOperationHandler, IModelKeyResolverHandler
    {
        private readonly ModelContextProvider _models;
        private readonly BimBridgeSettings _settings;
        private readonly ResultFileWriter _resultWriter;
        public LoadElementPropertiesOperationHandler(ModelContextProvider models)
            : this(models, new BimBridgeSettings()) { }
        public LoadElementPropertiesOperationHandler(
            ModelContextProvider models,
            BimBridgeSettings settings,
            ResultFileWriter resultWriter = null)
        {
            _models = models;
            _settings = settings ?? new BimBridgeSettings();
            _resultWriter = resultWriter ?? new ResultFileWriter(_settings);
        }
        public IReadOnlyCollection<string> OperationCodes { get; } = new[] { "load-element-properties" };
        public string Capability => "load-element-properties";
        public bool IsAvailable => _models.HasModelServices;

        public async Task<object> ExecuteAsync(TaskExecutionContext context)
        {
            var request = BimOperationDispatcher.ReadPayload<LoadPropertiesRequest>(context.Assignment);
            request.TargetModelKey = string.IsNullOrWhiteSpace(request.TargetModelKey)
                ? context.Assignment.TargetModelKey
                : request.TargetModelKey;
            if (request.ElementIds == null || request.ElementIds.Count == 0)
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "ElementIds is required.");
            if (request.ElementIds.Count > _settings.ResourceLimits.MaxPropertyElements)
                throw new BimBridgeException(
                    BimBridgeErrorCodes.PayloadTooLarge,
                    string.Format(
                        "ElementIds contains {0} items; the configured limit is {1}.",
                        request.ElementIds.Count,
                        _settings.ResourceLimits.MaxPropertyElements));
            var batchSize = request.BatchSize <= 0 ? 100 : Math.Min(request.BatchSize, 1000);
            var model = await _models.GetModelAsync(
                request.TargetModelKey,
                request.ModelVersionUtc,
                context.CancellationToken).ConfigureAwait(false);
            context.Resources?.Track(model);
            var requestedNames = request.PropertyNames == null
                ? null
                : new HashSet<string>(request.PropertyNames.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            var failed = new List<ElementKey>();
            var warnings = new List<string>();
            var elementErrors = new List<ElementReadError>();
            var emptyProperties = 0;
            var filteredProperties = 0;
            using (model)
            {
                await _models.ResolveModelPartsAsync(
                    model.ModelId,
                    request.ElementIds.Select(x => x.ModelPartId),
                    false,
                    context.CancellationToken).ConfigureAwait(false);

                return await SnapshotResultPublisher.WriteAsync(_resultWriter, model,
                    context.Assignment.TaskId,
                    async writer =>
                    {
                        var serializer = JsonSerializer.CreateDefault();
                        writer.WriteStartObject();
                        writer.WritePropertyName("ModelKey");
                        writer.WriteValue(model.ModelId.ToString("D"));
                        writer.WritePropertyName("ModelVersionUtc");
                        writer.WriteValue(model.ModelVersionUtc);
                        writer.WritePropertyName("TargetModelPartIds");
                        writer.WriteStartArray();
                        foreach (var partId in model.TargetModelPartIds) writer.WriteValue(partId);
                        writer.WriteEndArray();
                        writer.WritePropertyName("Elements");
                        writer.WriteStartArray();

                        for (var offset = 0; offset < request.ElementIds.Count; offset += batchSize)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            var batch = request.ElementIds.Skip(offset).Take(batchSize);
                            var processed = 0;
                            foreach (var key in batch)
                            {
                                context.CancellationToken.ThrowIfCancellationRequested();
                                processed++;
                                try
                                {
                                    if (!_models.IsPartInScope(model.ModelId, model.TargetModelPartIds, key.ModelPartId))
                                    {
                                        failed.Add(key);
                                        elementErrors.Add(new ElementReadError { ModelPartId = key.ModelPartId, ElementId = key.ElementId,
                                            Code = "PART_NOT_AVAILABLE", Message = "Part is outside the requested model scope." });
                                        warnings.Add(string.Format(
                                            "{0:D}/{1:D}: model part is outside the requested coordination model scope.",
                                            key.ModelPartId,
                                            key.ElementId));
                                        continue;
                                    }

                                    if (!await model.HasPartAsync(key.ModelPartId, context.CancellationToken).ConfigureAwait(false))
                                        throw new BimBridgeException("PART_NOT_AVAILABLE", "Part is absent from storage.", true);
                                    var plain = await model.Sdk.ReadAsync(storage => SnapshotElementReader.ReadProperties(
                                        storage, key, model.Version, requestedNames, context.CancellationToken), context.CancellationToken).ConfigureAwait(false);
                                    if (!plain.Exists)
                                        throw new BimBridgeException("ELEMENT_NOT_FOUND", "Element is absent from the selected snapshot.");
                                    if (plain.TotalPropertyCount == 0) emptyProperties++;
                                    else if (plain.SelectedPropertyCount == 0) filteredProperties++;
                                    serializer.Serialize(writer, new
                                    {
                                        key.ModelPartId, key.ElementId, plain.PropertySets
                                    });
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception exception)
                                {
                                    failed.Add(key);
                                    warnings.Add($"{key.ModelPartId:D}/{key.ElementId:D}: {exception.Message}");
                                    elementErrors.Add(new ElementReadError { ModelPartId = key.ModelPartId, ElementId = key.ElementId,
                                        Code = (exception as BimBridgeException)?.Code ?? "ELEMENT_READ_FAILED", Message = exception.Message });
                                }
                            }
                            context.ReportProgress?.Invoke(
                                Math.Min(100, (offset + processed) * 100 / request.ElementIds.Count),
                                "Р—Р°РіСЂСѓР·РєР° СЃРІРѕР№СЃС‚РІ");
                        }

                        writer.WriteEndArray();
                        writer.WritePropertyName("FailedElementIds");
                        serializer.Serialize(writer, failed);
                        writer.WritePropertyName("Warnings");
                        serializer.Serialize(writer, warnings);
                        writer.WritePropertyName("ElementErrors");
                        serializer.Serialize(writer, elementErrors);
                        writer.WritePropertyName("EmptyPropertyElementCount");
                        writer.WriteValue(emptyProperties);
                        writer.WritePropertyName("FilteredPropertyElementCount");
                        writer.WriteValue(filteredProperties);
                        await model.AssertUnchangedAsync(context.CancellationToken).ConfigureAwait(false);
                        System.Diagnostics.Trace.TraceInformation("BIM properties model={0} version={1} requested={2} failed={3} empty={4} filtered={5}",
                            model.ModelId, model.ModelVersionUtc, request.ElementIds.Count, failed.Count, emptyProperties, filteredProperties);
                        _models.LogReadSummary("load-element-properties", model, request.ElementIds.Select(x => x.ModelPartId), request.ElementIds.Count, failed.Count, emptyProperties, filteredProperties);
                        writer.WriteEndObject();
                    },
                    context.CancellationToken).ConfigureAwait(false);
            }
        }

        private static object SafeValue(object value)
        {
            if (value == null || value is string || value is bool || value is byte || value is short ||
                value is int || value is long || value is float || value is double || value is decimal ||
                value is DateTime || value is Guid)
                return value;
            return value.ToString();
        }
    }

    public sealed class ElementRelationsRequest
    {
        public string TargetModelKey { get; set; }
        public DateTime? ModelVersionUtc { get; set; }
        public List<ElementKey> ElementIds { get; set; } = new List<ElementKey>();
        public bool IncludeParent { get; set; } = true;
        public bool IncludeChildren { get; set; } = true;
        public int MaxChildren { get; set; } = 10000;
    }

    public sealed class ModelElementDescriptor
    {
        public Guid ModelPartId { get; set; }
        public Guid ElementId { get; set; }
        public Guid? ParentModelPartId { get; set; }
        public Guid? ParentElementId { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string VersionUtc { get; set; }
    }

    public sealed class ElementRelationDescriptor
    {
        public ModelElementDescriptor Element { get; set; }
        public ModelElementDescriptor Parent { get; set; }
        public List<ModelElementDescriptor> Children { get; set; } = new List<ModelElementDescriptor>();
        public bool ChildrenTruncated { get; set; }
    }

    public sealed class GetElementRelationsOperationHandler : IBimOperationHandler, IModelKeyResolverHandler
    {
        private readonly ModelContextProvider _models;
        private readonly BimBridgeSettings _settings;
        private readonly ResultFileWriter _resultWriter;

        public GetElementRelationsOperationHandler(
            ModelContextProvider models,
            BimBridgeSettings settings,
            ResultFileWriter resultWriter = null)
        {
            _models = models ?? throw new ArgumentNullException(nameof(models));
            _settings = settings ?? new BimBridgeSettings();
            _resultWriter = resultWriter ?? new ResultFileWriter(_settings);
        }

        public IReadOnlyCollection<string> OperationCodes { get; } = new[] { "get-element-relations" };
        public string Capability => "get-element-relations";
        public bool IsAvailable => _models.HasModelServices;

        public async Task<object> ExecuteAsync(TaskExecutionContext context)
        {
            var request = BimOperationDispatcher.ReadPayload<ElementRelationsRequest>(context.Assignment);
            request.TargetModelKey = string.IsNullOrWhiteSpace(request.TargetModelKey)
                ? context.Assignment.TargetModelKey
                : request.TargetModelKey;

            if (request.ElementIds == null || request.ElementIds.Count == 0)
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "ElementIds is required.");
            if (!request.IncludeParent && !request.IncludeChildren)
                throw new BimBridgeException(
                    BimBridgeErrorCodes.InvalidRequest,
                    "IncludeParent or IncludeChildren must be enabled.");
            if (request.ElementIds.Any(x => x == null || x.ModelPartId == Guid.Empty || x.ElementId == Guid.Empty))
                throw new BimBridgeException(
                    BimBridgeErrorCodes.InvalidRequest,
                    "Every element must contain non-empty ModelPartId and ElementId values.");
            if (request.ElementIds.Count > _settings.ResourceLimits.MaxPropertyElements)
                throw new BimBridgeException(
                    BimBridgeErrorCodes.PayloadTooLarge,
                    string.Format(
                        "ElementIds contains {0} items; the configured limit is {1}.",
                        request.ElementIds.Count,
                        _settings.ResourceLimits.MaxPropertyElements));

            var maxChildren = request.MaxChildren <= 0
                ? Math.Min(10000, _settings.ResourceLimits.MaxSearchResults)
                : Math.Min(request.MaxChildren, _settings.ResourceLimits.MaxSearchResults);
            var model = await _models.GetModelAsync(
                request.TargetModelKey,
                request.ModelVersionUtc,
                context.CancellationToken).ConfigureAwait(false);
            context.Resources?.Track(model);

            using (model)
            {
                await _models.ResolveModelPartsAsync(
                    model.ModelId,
                    request.ElementIds.Select(x => x.ModelPartId),
                    false,
                    context.CancellationToken).ConfigureAwait(false);

                return await SnapshotResultPublisher.WriteAsync(_resultWriter, model,
                    context.Assignment.TaskId,
                    async writer =>
                    {
                        var serializer = JsonSerializer.CreateDefault();
                        var failed = new List<ElementKey>();
                        var warnings = new List<string>();
                        var elementErrors = new List<ElementReadError>();
                        var totalChildren = 0;
                        var truncated = false;

                        writer.WriteStartObject();
                        writer.WritePropertyName("ModelKey");
                        writer.WriteValue(model.ModelId.ToString("D"));
                        writer.WritePropertyName("ModelVersionUtc");
                        writer.WriteValue(model.ModelVersionUtc);
                        writer.WritePropertyName("Elements");
                        writer.WriteStartArray();

                        for (var index = 0; index < request.ElementIds.Count; index++)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            var key = request.ElementIds[index];
                            if (!_models.IsPartInScope(model.ModelId, model.TargetModelPartIds, key.ModelPartId))
                            {
                                failed.Add(key);
                                elementErrors.Add(new ElementReadError { ModelPartId = key.ModelPartId, ElementId = key.ElementId,
                                    Code = "PART_NOT_AVAILABLE", Message = "Part is outside the requested model scope." });
                                warnings.Add(string.Format(
                                    "{0:D}/{1:D}: model part is outside the requested coordination model scope.",
                                    key.ModelPartId,
                                    key.ElementId));
                                continue;
                            }

                            try
                            {
                                if (!await model.HasPartAsync(key.ModelPartId, context.CancellationToken).ConfigureAwait(false))
                                    throw new BimBridgeException("PART_NOT_AVAILABLE", "Part is absent from storage.", true);
                                var relation = await model.Sdk.ReadAsync(storage => SnapshotElementReader.ReadRelations(
                                    storage, key, model.Version, request.IncludeParent, request.IncludeChildren,
                                    maxChildren - totalChildren, warnings, context.CancellationToken), context.CancellationToken).ConfigureAwait(false);
                                totalChildren += relation.Children.Count;
                                truncated |= relation.ChildrenTruncated;
                                serializer.Serialize(writer, relation);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception exception)
                            {
                                failed.Add(key);
                                warnings.Add(string.Format(
                                    "{0:D}/{1:D}: {2}",
                                    key.ModelPartId,
                                    key.ElementId,
                                    exception.Message));
                                elementErrors.Add(new ElementReadError { ModelPartId = key.ModelPartId, ElementId = key.ElementId,
                                    Code = (exception as BimBridgeException)?.Code ?? "ELEMENT_READ_FAILED", Message = exception.Message });
                            }

                            context.ReportProgress?.Invoke(
                                Math.Min(100, (index + 1) * 100 / request.ElementIds.Count),
                                "Р—Р°РіСЂСѓР·РєР° СЃРІСЏР·РµР№ СЌР»РµРјРµРЅС‚РѕРІ");
                        }

                        writer.WriteEndArray();
                        writer.WritePropertyName("ChildrenCount");
                        writer.WriteValue(totalChildren);
                        writer.WritePropertyName("Truncated");
                        writer.WriteValue(truncated);
                        writer.WritePropertyName("FailedElementIds");
                        serializer.Serialize(writer, failed);
                        writer.WritePropertyName("Warnings");
                        serializer.Serialize(writer, warnings);
                        writer.WritePropertyName("ElementErrors");
                        serializer.Serialize(writer, elementErrors);
                        await model.AssertUnchangedAsync(context.CancellationToken).ConfigureAwait(false);
                        System.Diagnostics.Trace.TraceInformation("BIM relations model={0} version={1} requested={2} failed={3}",
                            model.ModelId, model.ModelVersionUtc, request.ElementIds.Count, failed.Count);
                        _models.LogReadSummary("get-element-relations", model, request.ElementIds.Select(x => x.ModelPartId), request.ElementIds.Count, failed.Count);
                        writer.WriteEndObject();
                    },
                    context.CancellationToken).ConfigureAwait(false);
            }
        }

        internal static ModelElementDescriptor CreateDescriptor(IModelElement element)
        {
            if (element == null || !IsValid(element.Id)) return null;
            var parent = IsValid(element.ParentId) ? element.ParentId : null;
            return new ModelElementDescriptor
            {
                ModelPartId = element.Id.ModelPartId,
                ElementId = element.Id.ElementId,
                ParentModelPartId = parent?.ModelPartId,
                ParentElementId = parent?.ElementId,
                Name = element.Name,
                Type = element.Type,
                VersionUtc = element.Version == DateTime.MinValue
                    ? null
                    : element.Version.ToUniversalTime().ToString("O")
            };
        }

        private static bool IsValid(IModelElementId value)
        {
            return value != null && value.ElementId != Guid.Empty;
        }
    }

    public sealed class DiagnosticOperationHandler : IBimOperationHandler
    {
        private readonly bool _enabled;
        public DiagnosticOperationHandler(bool enabled) { _enabled = enabled; }
        public IReadOnlyCollection<string> OperationCodes { get; } =
            new[] { "echo", "delay", "generate-test-result", "fail-test" };
        public string Capability => "diagnostics";
        public bool IsAvailable => _enabled;

        public async Task<object> ExecuteAsync(TaskExecutionContext context)
        {
            var payload = BimOperationDispatcher.ReadPayload<DiagnosticPayload>(context.Assignment);
            switch (context.Assignment.OperationCode.ToLowerInvariant())
            {
                case "echo":
                    return new { payload.Message, ReceivedAtUtc = DateTime.UtcNow };
                case "delay":
                    await Task.Delay(Math.Max(0, Math.Min(payload.DelayMilliseconds, 300000)), context.CancellationToken)
                        .ConfigureAwait(false);
                    return new { DelayedMilliseconds = payload.DelayMilliseconds };
                case "generate-test-result":
                    return new { Data = new string('x', Math.Max(0, Math.Min(payload.Size, 10 * 1024 * 1024))) };
                case "fail-test":
                    throw new BimBridgeException("TEST_FAILURE", payload.Message ?? "Requested test failure.");
                default:
                    throw new BimBridgeException("CAPABILITY_NOT_AVAILABLE", "Diagnostic operation is unavailable.");
            }
        }

        private sealed class DiagnosticPayload
        {
            public string Message { get; set; }
            public int DelayMilliseconds { get; set; }
            public int Size { get; set; }
        }
    }
}
