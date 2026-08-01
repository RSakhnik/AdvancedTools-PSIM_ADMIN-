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
            var models = await _models.GetModelsAsync().ConfigureAwait(false);
            return new
            {
                GeneratedAtUtc = DateTime.UtcNow,
                ActiveModelKey = models.FirstOrDefault(x => x.IsActive)?.ModelKey,
                Models = models
            };
        }
    }

    public sealed class SearchElementsRequest
    {
        public string TargetModelKey { get; set; }
        public List<Guid> TargetModelPartIds { get; set; }
        public string LogicalOperator { get; set; } = "And";
        public List<SearchConditionRequest> Conditions { get; set; } = new List<SearchConditionRequest>();
        public int MaxResults { get; set; } = 10000;
    }

    public sealed class SearchConditionRequest
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Operator { get; set; }
        public JToken Value { get; set; }
    }

    public sealed class ElementKey
    {
        public Guid ModelPartId { get; set; }
        public Guid ElementId { get; set; }
    }

    public sealed class SearchElementsOperationHandler : IBimOperationHandler, IModelKeyResolverHandler
    {
        private readonly ModelContextProvider _models;
        private readonly IModelSearchManager _searchManager;

        public SearchElementsOperationHandler(ModelContextProvider models, IModelSearchManager searchManager)
        {
            _models = models;
            _searchManager = searchManager;
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
            var model = await _models.GetModelAsync(
                request.TargetModelKey,
                context.CancellationToken).ConfigureAwait(false);
            if (request.Conditions == null || request.Conditions.Count == 0)
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "At least one search condition is required.");
            var max = request.MaxResults <= 0 ? 10000 : Math.Min(request.MaxResults, 100000);
            context.ReportProgress?.Invoke(10, "Подготовка поиска");

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
                : model.TargetModelPartIds.ToArray();
            var service = await _models.GetSearchServiceAsync(
                model,
                effectiveParts,
                context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            var found = await Task.Run(
                () => service.Search(state, max + 1).Take(max + 1).ToArray(),
                context.CancellationToken).ConfigureAwait(false);
            var truncated = found.Length > max;
            context.ReportProgress?.Invoke(100, "Поиск завершен");
            return new
            {
                ModelKey = model.ModelId.ToString("D"),
                TargetModelPartIds = effectiveParts,
                Count = Math.Min(found.Length, max),
                Truncated = truncated,
                Elements = found.Take(max).Select(x => new ElementKey
                {
                    ModelPartId = x.ModelPartId,
                    ElementId = x.ElementId
                }).ToArray()
            };
        }

        private static SearchSetState BuildSearchState(SearchElementsRequest request)
        {
            if (!Enum.TryParse(request.LogicalOperator, true, out SearchSetLogicalOperator logical))
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "LogicalOperator must be And or Or.");
            var root = new SearchSetGroupState
            {
                ItemType = SearchSetItemType.Group,
                GroupingOperator = logical,
                Items = new List<SearchSetStateItem>()
            };
            foreach (var condition in request.Conditions)
            {
                if (string.IsNullOrWhiteSpace(condition.Name))
                    throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "Search condition Name is required.");
                if (!Enum.TryParse(condition.DataType, true, out PropertyDataType dataType))
                    throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, $"Unknown DataType '{condition.DataType}'.");
                if (!Enum.TryParse(condition.Operator, true, out SearchSetEqualityOperator equality))
                    throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, $"Unknown Operator '{condition.Operator}'.");
                root.Items.Add(new SearchSetExpressionState
                {
                    ItemType = SearchSetItemType.Expression,
                    Category = condition.Category,
                    Field = condition.Name,
                    DataType = dataType,
                    EqualityOperator = equality,
                    Value = ConvertValue(condition.Value, dataType, equality)
                });
            }
            return new SearchSetState { RootGroup = root };
        }

        private static object ConvertValue(JToken token, PropertyDataType type, SearchSetEqualityOperator equality)
        {
            if (equality == SearchSetEqualityOperator.Defined || equality == SearchSetEqualityOperator.NotDefined)
                return null;
            if (token == null || token.Type == JTokenType.Null)
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "Search condition Value is required.");
            try
            {
                switch (type)
                {
                    case PropertyDataType.Boolean: return token.Value<bool>();
                    case PropertyDataType.Long: return token.Value<long>();
                    case PropertyDataType.Double: return token.Value<double>();
                    case PropertyDataType.DateTime: return token.Value<DateTime>().ToUniversalTime();
                    case PropertyDataType.Guid: return token.Value<Guid>();
                    default: return token.Value<string>();
                }
            }
            catch (Exception exception)
            {
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "Search condition Value has an invalid type.", false, exception);
            }
        }
    }

    public sealed class LoadPropertiesRequest
    {
        public string TargetModelKey { get; set; }
        public List<ElementKey> ElementIds { get; set; } = new List<ElementKey>();
        public List<string> PropertyNames { get; set; }
        public int BatchSize { get; set; } = 100;
    }

    internal sealed class ModelElementId : IModelElementId
    {
        public ModelElementId(Guid modelPartId, Guid elementId)
        {
            ModelPartId = modelPartId;
            ElementId = elementId;
        }
        public Guid ElementId { get; }
        public Guid ModelPartId { get; }
    }

    public sealed class LoadElementPropertiesOperationHandler : IBimOperationHandler, IModelKeyResolverHandler
    {
        private readonly ModelContextProvider _models;
        public LoadElementPropertiesOperationHandler(ModelContextProvider models) { _models = models; }
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
            var batchSize = request.BatchSize <= 0 ? 100 : Math.Min(request.BatchSize, 1000);
            var model = await _models.GetModelAsync(
                request.TargetModelKey,
                context.CancellationToken).ConfigureAwait(false);
            var requestedNames = request.PropertyNames == null
                ? null
                : new HashSet<string>(request.PropertyNames.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            var results = new List<object>();
            var failed = new List<ElementKey>();
            var warnings = new List<string>();
            await _models.ResolveModelPartsAsync(
                model.ModelId,
                request.ElementIds.Select(x => x.ModelPartId),
                false,
                context.CancellationToken).ConfigureAwait(false);

            for (var offset = 0; offset < request.ElementIds.Count; offset += batchSize)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var batch = request.ElementIds.Skip(offset).Take(batchSize).ToArray();
                await Task.Run(() =>
                {
                    foreach (var key in batch)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            if (!_models.IsPartInScope(model.ModelId, model.TargetModelPartIds, key.ModelPartId))
                            {
                                failed.Add(key);
                                warnings.Add(string.Format(
                                    "{0:D}/{1:D}: model part is outside the requested coordination model scope.",
                                    key.ModelPartId,
                                    key.ElementId));
                                continue;
                            }
                            var sets = model.Storage.LoadElementProperties(
                                new ModelElementId(key.ModelPartId, key.ElementId), model.Version) ?? Enumerable.Empty<IPropertySet>();
                            var mapped = sets.Select(set => new
                            {
                                Category = set.Name,
                                Type = set.Type,
                                Properties = set.Properties
                                    .Where(p => requestedNames == null || requestedNames.Count == 0 || requestedNames.Contains(p.Name))
                                    .Select(p => new { p.Name, p.Unit, Value = SafeValue(p.Value) })
                                    .ToArray()
                            }).ToArray();
                            results.Add(new { key.ModelPartId, key.ElementId, PropertySets = mapped });
                        }
                        catch (Exception exception)
                        {
                            failed.Add(key);
                            warnings.Add($"{key.ModelPartId:D}/{key.ElementId:D}: {exception.Message}");
                        }
                    }
                }, context.CancellationToken).ConfigureAwait(false);
                context.ReportProgress?.Invoke(
                    Math.Min(100, (offset + batch.Length) * 100 / request.ElementIds.Count),
                    "Загрузка свойств");
            }

            return new
            {
                ModelKey = model.ModelId.ToString("D"),
                TargetModelPartIds = model.TargetModelPartIds,
                Elements = results,
                FailedElementIds = failed,
                Warnings = warnings
            };
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
