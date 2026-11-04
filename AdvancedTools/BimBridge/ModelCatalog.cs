using Ascon.Pilot.Bim.SDK;
using Ascon.Pilot.Bim.SDK.Model;
using Ascon.Pilot.Bim.SDK.ModelStorage;
using Ascon.Pilot.Bim.SDK.ModelViewer;
using Ascon.Pilot.Bim.SDK.Search;
using Ascon.Pilot.SDK;
using Pilot.BimBridge.Contracts;
using PilotDataObject = Ascon.Pilot.SDK.IDataObject;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AdvancedTools.BimBridge
{
    public sealed class ModelPartDescriptor
    {
        public Guid ModelPartId { get; set; }
        public string DisplayName { get; set; }
        public string TypeName { get; set; }
        public bool IsLoaded { get; set; }
    }

    public sealed class ProjectDescriptor
    {
        public Guid ProjectId { get; set; }
        public string DisplayName { get; set; }
        public string ParentId { get; set; }
    }

    public sealed class ModelDescriptor
    {
        public Guid ModelId { get; set; }
        public string ModelKey { get; set; }
        public string DisplayName { get; set; }
        public Guid ProjectId { get; set; }
        public string ProjectName { get; set; }
        public string ProjectPath { get; set; }
        public string VersionUtc { get; set; }
        public string DataVersionUtc { get; set; }
        public string ViewerVersionUtc { get; set; }
        public bool IsActive { get; set; }
        public bool IsOpen { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsStorageReady { get; set; }
        public bool IsSearchReady { get; set; }
        public string StorageState { get; set; }
        public string SearchState { get; set; }
        public string LastServiceError { get; set; }
        public string PartsDiscoveryState { get; set; }
        public int PartsCount { get; set; }
        public Guid[] ModelPartIds { get; set; }
        public ModelPartDescriptor[] ModelParts { get; set; }
    }

    public sealed class CatalogReadinessSnapshot
    {
        public CatalogReadinessSnapshot(string state, bool isComplete, int unresolvedPartCount, IEnumerable<string> warnings)
        {
            State = string.IsNullOrWhiteSpace(state) ? "partial" : state;
            IsComplete = isComplete;
            UnresolvedPartCount = Math.Max(0, unresolvedPartCount);
            Warnings = (warnings ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        }

        public string State { get; }
        public bool IsComplete { get; }
        public int UnresolvedPartCount { get; }
        public string[] Warnings { get; }
    }

    public sealed class CatalogProgressSnapshot
    {
        public CatalogProgressSnapshot(bool isRunning, int completed, int total, int percent, string description)
        {
            IsRunning = isRunning;
            Completed = completed;
            Total = total;
            Percent = Math.Max(0, Math.Min(100, percent));
            Description = description ?? string.Empty;
        }

        public bool IsRunning { get; }
        public int Completed { get; }
        public int Total { get; }
        public int Percent { get; }
        public string Description { get; }
        public bool IsIndeterminate => IsRunning && Total <= 0;
    }

    public static class BimCatalogIdentityRules
    {
        public static bool IsExactCoordinationModel(string typeName) =>
            string.Equals(typeName, TypeNames.CoordinationModel, StringComparison.Ordinal);

        public static bool IsSupportedModelPart(string typeName) =>
            string.Equals(typeName, TypeNames.ModelPart, StringComparison.Ordinal) ||
            string.Equals(typeName, TypeNames.ModelPartCloudPoint, StringComparison.Ordinal) ||
            string.Equals(typeName, "bim_modelPart_other3Dmodel", StringComparison.OrdinalIgnoreCase);

        public static Guid? GetRootOwner(Guid id, string typeName) =>
            id != Guid.Empty && IsExactCoordinationModel(typeName) ? (Guid?)id : null;
    }

    public sealed class CatalogSearchAccumulator
    {
        private readonly object _gate = new object();
        private readonly HashSet<Guid> _ids = new HashSet<Guid>();
        private long _total = -1;
        private bool _remoteReceived;

        public void Apply(IEnumerable<Guid> ids, long total, bool isRemote)
        {
            lock (_gate)
            {
                foreach (var id in ids ?? Enumerable.Empty<Guid>())
                    if (id != Guid.Empty) _ids.Add(id);
                _total = Math.Max(_total, total);
                _remoteReceived |= isRemote;
            }
        }

        public bool IsComplete(int maxResults)
        {
            lock (_gate)
                return _remoteReceived &&
                    _total >= 0 &&
                    _total <= maxResults &&
                    _ids.Count >= _total;
        }

        public bool HasExpectedResult(int maxResults)
        {
            lock (_gate)
            {
                var expected = _total < 0 ? long.MaxValue : Math.Min(_total, maxResults);
                return _remoteReceived && _ids.Count >= expected;
            }
        }

        public long Total
        {
            get { lock (_gate) return _total; }
        }

        public Guid[] GetIds()
        {
            lock (_gate) return _ids.OrderBy(x => x).ToArray();
        }
    }

    public sealed class ModelAccess : IDisposable
    {
        private readonly Action<IModelStorage> _release;
        private IModelStorage _storage;
        private int _disposed;

        public ModelAccess(
            Guid modelId,
            DateTime version,
            IModelStorage storage,
            IReadOnlyCollection<Guid> targetModelPartIds,
            Action<IModelStorage> release = null,
            Func<Func<object>, Task<object>> dispatch = null,
            DateTime? initialLatestVersion = null,
            DateTime? viewerVersion = null,
            Func<Func<object>, CancellationToken, Task<object>> dispatchWithToken = null,
            Action<IDisposable> releaseLate = null)
        {
            ModelId = modelId;
            Version = version;
            _storage = storage;
            TargetModelPartIds = targetModelPartIds ?? Array.Empty<Guid>();
            _release = release;
            Sdk = new ModelSdkAdapter(storage, dispatch, dispatchWithToken, releaseLate);
            InitialLatestVersion = initialLatestVersion ?? version;
            ViewerVersion = viewerVersion;
        }

        public Guid ModelId { get; }
        public DateTime Version { get; }
        public DateTime InitialLatestVersion { get; }
        public DateTime? ViewerVersion { get; }
        public ModelSdkAdapter Sdk { get; }
        public string ModelVersionUtc => Version.ToUniversalTime().ToString("O");
        public Task AssertUnchangedAsync(CancellationToken token) => Sdk.ReadAsync(storage =>
        {
            var versions = (storage.GetVersions() ?? Enumerable.Empty<DateTime>()).ToArray();
            ModelSnapshotPolicy.AssertUnchanged(InitialLatestVersion, versions);
            if (!versions.Any(x => x.ToUniversalTime() == Version.ToUniversalTime()))
                throw new BimBridgeException("MODEL_CHANGED", "Selected snapshot was removed during the operation.", true);
            System.Diagnostics.Trace.TraceInformation("BIM snapshot model={0} version={1} viewer={2}", ModelId, ModelVersionUtc, ViewerVersion);
            return true;
        }, token);
        public Task<bool> HasPartAsync(Guid partId, CancellationToken token) => Sdk.ReadAsync(
            storage => (storage.GetModelPartsIds() ?? Enumerable.Empty<Guid>()).Contains(partId), token);
        public IModelStorage Storage => _storage;
        public IReadOnlyCollection<Guid> TargetModelPartIds { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var storage = Interlocked.Exchange(ref _storage, null);
            Sdk.Detach();
            if (storage == null) return;
            if (_release != null) _release(storage);
            else storage.Dispose();
        }
    }

    public sealed class SearchTargetAccess
    {
        public SearchTargetAccess(Guid modelId, IReadOnlyCollection<Guid> targetModelPartIds)
        {
            ModelId = modelId;
            TargetModelPartIds = targetModelPartIds ?? Array.Empty<Guid>();
        }

        public Guid ModelId { get; }
        public IReadOnlyCollection<Guid> TargetModelPartIds { get; }
    }

    public sealed class SearchServiceLease : IDisposable
    {
        private readonly Action<IModelSearchService> _release;
        private IModelSearchService _service;
        private int _disposed;

        public SearchServiceLease(IModelSearchService service, Action<IModelSearchService> release)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        public IModelSearchService Service => _service;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var service = Interlocked.Exchange(ref _service, null);
            if (service != null) _release(service);
        }
    }

    public sealed partial class ModelContextProvider : IDisposable
    {
        private const int SubscribeChunkSize = 200;
        // Pilot owns substantial native/runtime caches behind IModelStorage. A
        // sequential one-shot probe keeps the peak bounded and, unlike the old
        // four-way minute polling, does not continuously repopulate those caches.
        private const int WarmupParallelism = 1;
        private const int CatalogSearchMaxResults = 100000;
        private static readonly TimeSpan SubscribeTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan CatalogSearchTimeout = TimeSpan.FromSeconds(90);
        // A cold storage can download its index for longer than five seconds.
        private static readonly TimeSpan StorageLoadTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SearchServiceLoadTimeout = TimeSpan.FromSeconds(30);

        private readonly object _gate = new object();
        private readonly IObjectsRepository _objectsRepository;
        private readonly Dictionary<Guid, DateTime> _dataVersions = new Dictionary<Guid, DateTime>();
        private string DataVersion(Guid modelId)
        {
            lock (_gate) return _dataVersions.TryGetValue(modelId, out var version) ? version.ToUniversalTime().ToString("O") : null;
        }
        private readonly ISearchService _pilotSearchService;
        private readonly IModelManager _modelManager;
        private readonly IModelStorageProvider _storageProvider;
        private readonly IModelSearchManager _searchManager;
        private readonly ITabServiceProvider _tabServiceProvider;
        private readonly BimBridgeSettings _settings;
        private readonly IBimBridgeLog _log;
        private readonly IBimBridgeResourceMonitor _resourceMonitor;
        private readonly ISearchIndexInventory _searchIndexInventory;
        private readonly AdaptiveResourcePolicy _resourcePolicy;
        private readonly Dispatcher _uiDispatcher;
        private readonly SdkCallGateway _sdkGateway;
        private readonly PilotUiMonitor _pilotUi;
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _warmupGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _catalogSearchGate = new SemaphoreSlim(2, 2);
        private readonly SemaphoreSlim _searchInitializationGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly CoalescingSignal _catalogChangedSignal;
        private readonly CoalescingSignal _modelEventSignal;
        private readonly object _modelEventGate = new object();
        private readonly HashSet<Guid> _pendingModelEventIds = new HashSet<Guid>();
        private readonly Dictionary<Guid, CatalogModel> _models = new Dictionary<Guid, CatalogModel>();
        private readonly Dictionary<Guid, CatalogProject> _projects = new Dictionary<Guid, CatalogProject>();
        private readonly HashSet<Guid> _warmupAttempted = new HashSet<Guid>();
        private readonly Dictionary<Guid, Guid> _partToModel = new Dictionary<Guid, Guid>();
        private string _loadedProjectFilterKey = "";
        private int _warmupGeneration;
        private int _activeStorages;
        private int _activeSearchServices;
        private string _resourceRestrictionReason = string.Empty;
        private bool _existingIndexesOnly;
        private TaskCompletionSource<bool> _partsCatalogReady = NewReadinessSource();
        private bool _partsCatalogComplete;
        private int _unresolvedPartCount;
        private string[] _catalogWarnings = Array.Empty<string>();
        private int _catalogProgressCompleted;
        private int _catalogProgressTotal;
        private bool _catalogProgressRunning;
        private string _catalogProgressDescription = "Каталог ещё не обновлялся";
        private long _fullCatalogRefreshCount;
        private long _selectiveCatalogRefreshCount;
        private long _storageProbeCount;
        private Task _backgroundTask;
        private bool _disposed;

        public ModelContextProvider(
            IObjectsRepository objectsRepository,
            ISearchService pilotSearchService,
            IModelManager modelManager,
            IModelStorageProvider storageProvider,
            IModelSearchManager searchManager,
            ITabServiceProvider tabServiceProvider,
            BimBridgeSettings settings,
            IBimBridgeLog log,
            IBimBridgeResourceMonitor resourceMonitor = null,
            ISearchIndexInventory searchIndexInventory = null,
            AdaptiveResourcePolicy resourcePolicy = null)
        {
            _objectsRepository = objectsRepository;
            _pilotSearchService = pilotSearchService;
            _modelManager = modelManager;
            _storageProvider = storageProvider;
            _searchManager = searchManager;
            _tabServiceProvider = tabServiceProvider;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log;
            _resourceMonitor = resourceMonitor ?? new SystemBimBridgeResourceMonitor();
            _searchIndexInventory = searchIndexInventory ?? new FileSystemSearchIndexInventory();
            _resourcePolicy = resourcePolicy ?? new AdaptiveResourcePolicy();
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            _sdkGateway = new SdkCallGateway(_uiDispatcher, settings, log);
            _pilotUi = new PilotUiMonitor(_uiDispatcher, log);
            _catalogChangedSignal = new CoalescingSignal(
                TimeSpan.FromMilliseconds(250),
                () => CatalogChanged?.Invoke(this, EventArgs.Empty));
            _modelEventSignal = new CoalescingSignal(
                TimeSpan.FromSeconds(1),
                () => _ = Task.Run(() => ProcessModelEventsAsync(_lifetime.Token)));

            if (_modelManager != null)
            {
                _modelManager.ModelOpened += OnModelChanged;
                _modelManager.ModelLoaded += OnModelChanged;
                _modelManager.ModelClosed += OnModelChanged;
            }

            _backgroundTask = Task.Run(() => InitializeCatalogAsync(_lifetime.Token));
        }

        public event EventHandler CatalogChanged;

        public string SdkCounterDiagnostics => "SDK gateway (process-wide): calls="+_sdkGateway.SdkReadCalls+
            "; dispatcherInvocations="+_sdkGateway.DispatcherInvocations;
        public bool HasModelServices =>
            _objectsRepository != null && _modelManager != null && _storageProvider != null;

        public string[] GetModelKeys()
        {
            lock (_gate)
            {
                return _models.Keys
                    .Concat(_partToModel.Keys)
                    .Distinct()
                    .Select(x => x.ToString("D"))
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        public CatalogProgressSnapshot GetCatalogProgress()
        {
            lock (_gate)
            {
                var percent = _catalogProgressTotal > 0
                    ? (int)Math.Round(_catalogProgressCompleted * 100d / _catalogProgressTotal)
                    : (!_catalogProgressRunning && _models.Count > 0 ? 100 : 0);
                return new CatalogProgressSnapshot(
                    _catalogProgressRunning,
                    _catalogProgressCompleted,
                    _catalogProgressTotal,
                    percent,
                    _catalogProgressDescription);
            }
        }

        public CatalogReadinessSnapshot GetCatalogReadiness()
        {
            lock (_gate)
                return new CatalogReadinessSnapshot(
                    _partsCatalogComplete ? "complete" : "partial",
                    _partsCatalogComplete,
                    _unresolvedPartCount,
                    _catalogWarnings);
        }

        public async Task<CatalogReadinessSnapshot> WaitForPartsCatalogAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Task ready;
            lock (_gate) ready = _partsCatalogReady.Task;
            var delay = Task.Delay(timeout, cancellationToken);
            await Task.WhenAny(ready, delay).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return GetCatalogReadiness();
        }

        public IReadOnlyList<ProjectDescriptor> GetProjects()
        {
            lock (_gate)
            {
                return _projects.Values
                    .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.ProjectId)
                    .Select(x => new ProjectDescriptor
                    {
                        ProjectId = x.ProjectId,
                        DisplayName = x.DisplayName,
                        ParentId = x.ParentId == Guid.Empty ? null : x.ParentId.ToString("D")
                    }).ToArray();
            }
        }

        public string GetResourceStatus()
        {
            var usage = _resourceMonitor.Capture();
            var decision = _resourcePolicy.Evaluate(
                usage,
                _settings.ResourceLimits,
                SearchResourceRequestKind.NewIndex);
            int activeStorages;
            int activeSearchServices;
            lock (_gate)
            {
                activeStorages = _activeStorages;
                activeSearchServices = _activeSearchServices;
                _resourceRestrictionReason = decision.IsAllowed ? string.Empty : decision.Reason;
                _existingIndexesOnly = decision.ExistingIndexesOnly;
            }
            var indexes = _searchIndexInventory.Capture();
            var status = string.Format(
                "Диск C: свободно {0} ГБ, резерв {1} ГБ. ОЗУ: свободно {2} ГБ, процесс {3} ГБ. Общий кэш Pilot SearchIndexes: {4} ГБ ({5}). Ресурсы worker: storage {6}, search services {7}; между заданиями не кэшируются. Каталог: полный поиск только при запуске и вручную, события обновляются адресно. Режим: {8}{9}.",
                FormatGigabytes(usage.IsDiskKnown ? (long?)usage.FreeDiskBytes : null),
                _settings.ResourceLimits.MinimumFreeDiskSpaceGb,
                FormatGigabytes(usage.IsMemoryKnown ? (long?)usage.AvailableMemoryBytes : null),
                FormatGigabytes(usage.ProcessPrivateBytes),
                FormatGigabytes(indexes.TotalLengthBytes),
                indexes.Count,
                activeStorages,
                activeSearchServices,
                decision.IsAllowed
                    ? (decision.ExistingIndexesOnly ? "только готовые индексы" : "без ограничений")
                    : "поиск ограничен",
                decision.IsAllowed ? string.Empty : " — " + decision.Reason);
            return status + " " + SdkStatus + string.Format(
                " Счётчики обслуживания: полных обновлений {0}, адресных {1}, storage-проб {2}.",
                Interlocked.Read(ref _fullCatalogRefreshCount),
                Interlocked.Read(ref _selectiveCatalogRefreshCount),
                Interlocked.Read(ref _storageProbeCount));
        }

        public void GetActiveResourceCounts(out int storages, out int searchServices)
        {
            lock (_gate)
            {
                storages = _activeStorages;
                searchServices = _activeSearchServices;
            }
        }

        public string[] GetDisplayItems()
        {
            lock (_gate)
            {
                return _models.Values
                    .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.ModelId)
                    .SelectMany(x =>
                    {
                        var rows = new List<string>
                        {
                            string.Format(
                                "{0} | {1:D} | частей: {2} | хранилище: {3} | поиск: {4}{5}",
                                x.DisplayName,
                                x.ModelId,
                                x.Parts.Count,
                                LocalizeServiceState(x.StorageState),
                                LocalizeServiceState(x.SearchState),
                                string.IsNullOrWhiteSpace(x.LastServiceError) ? "" : " | " + x.LastServiceError)
                        };
                        rows.AddRange(x.Parts.Values
                            .OrderBy(part => part.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                            .ThenBy(part => part.ModelPartId)
                            .Select(part => string.Format(
                                "    ↳ {0} | часть: {1:D} | {2}",
                                part.DisplayName,
                                part.ModelPartId,
                                LocalizePartType(part.TypeName))));
                        return rows;
                    })
                    .ToArray();
            }
        }

        private static string LocalizeServiceState(string state)
        {
            switch (state)
            {
                case ServiceStates.OnDemand: return "по запросу";
                case ServiceStates.Pending: return "ожидание";
                case ServiceStates.Loading: return "подключение";
                case ServiceStates.Ready: return "готово";
                case ServiceStates.Error: return "ошибка";
                default: return string.IsNullOrWhiteSpace(state) ? "неизвестно" : state;
            }
        }

        private static string LocalizePartType(string typeName)
        {
            if (string.Equals(typeName, TypeNames.ModelPart, StringComparison.Ordinal))
                return "часть BIM-модели";
            if (string.Equals(typeName, TypeNames.ModelPartCloudPoint, StringComparison.Ordinal))
                return "облако точек";
            if (string.Equals(typeName, "bim_modelPart_other3Dmodel", StringComparison.OrdinalIgnoreCase))
                return "сторонняя 3D-модель";
            return string.IsNullOrWhiteSpace(typeName) ? "часть модели" : typeName;
        }

        private string[] GetLegacyDisplayItems()
        {
            lock (_gate)
            {
                return _models.Values
                    .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.ModelId)
                    .SelectMany(x =>
                    {
                        var rows = new List<string>
                        {
                            string.Format(
                                "{0} | {1:D} | частей: {2} | storage: {3} | search: {4}{5}",
                                x.DisplayName,
                                x.ModelId,
                                x.Parts.Count,
                                x.StorageState,
                                x.SearchState,
                                string.IsNullOrWhiteSpace(x.LastServiceError) ? "" : " | " + x.LastServiceError)
                        };
                        rows.AddRange(x.Parts.Values
                            .OrderBy(part => part.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                            .ThenBy(part => part.ModelPartId)
                            .Select(part => string.Format(
                                "    ↳ {0} | часть: {1:D} | {2}",
                                part.DisplayName,
                                part.ModelPartId,
                                part.TypeName)));
                        return rows;
                    })
                    .ToArray();
            }
        }

        public async Task RefreshAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            ApplyResourceLimits();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken))
            {
                await _refreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    var refreshNumber = Interlocked.Increment(ref _fullCatalogRefreshCount);
                    _log?.Info("Full BIM catalog refresh started (#" + refreshNumber + ").");
                    lock (_gate)
                    {
                        _catalogProgressRunning = true;
                        _catalogProgressCompleted = 0;
                        _catalogProgressTotal = 0;
                        _catalogProgressDescription = "Поиск BIM-моделей и частей";
                        _partsCatalogComplete = false;
                        _unresolvedPartCount = 0;
                        _catalogWarnings = Array.Empty<string>();
                        if (_partsCatalogReady.Task.IsCompleted)
                            _partsCatalogReady = NewReadinessSource();
                    }
                    RaiseCatalogChanged();

                    var modelSearch = await FindObjectIdsAsync(
                        TypeNames.CoordinationModel,
                        "coordination models",
                        true,
                        null,
                        linked.Token);
                    var modelIds = modelSearch.Ids;
                    var modelCandidates = await Task.Run(
                        () => LoadSnapshots(modelIds, linked.Token),
                        linked.Token).ConfigureAwait(false);
                    var trueModelRoots = modelCandidates
                        .Where(x => x != null &&
                            !x.IsDeleted &&
                            !x.IsInRecycleBin &&
                            IsCoordinationModel(x.TypeName))
                        .GroupBy(x => x.Id)
                        .Select(x => x.First())
                        .ToArray();

                    // Pilot type filters may include derived BIM object types.  Build
                    // the project filter from exact CoordinationModel snapshots before
                    // traversing any children, otherwise a model part can become a
                    // self-owned, false coordination model.
                    var seed = new CatalogScan();
                    foreach (var root in trueModelRoots)
                    {
                        seed.RegisterObject(root);
                        seed.GetOrAddModel(root.Id, root.DisplayName);
                    }
                    if (modelCandidates.Count < modelIds.Length)
                        seed.AddMissingSnapshots(modelIds.Length - modelCandidates.Count);
                    await Task.Run(
                        () => EnrichModelPaths(seed, trueModelRoots, linked.Token),
                        linked.Token).ConfigureAwait(false);
                    var selectedProjectNames = new HashSet<string>(
                        _settings.GetLoadedProjectNames(),
                        StringComparer.CurrentCultureIgnoreCase);
                    var selectedModelIds = new HashSet<Guid>(seed.Models.Values
                        .Where(model => IsSelectedProject(model, selectedProjectNames))
                        .Select(model => model.ModelId));
                    var selectedRoots = trueModelRoots
                        .Where(root => selectedModelIds.Contains(root.Id))
                        .ToArray();

                    var scan = await Task.Run(
                        () => ScanTree(selectedRoots, linked.Token),
                        linked.Token).ConfigureAwait(false);
                    scan.AddMissingSnapshots(seed.MissingSnapshotCount);
                    await Task.Run(
                        () => EnrichModelPaths(scan, selectedRoots, linked.Token),
                        linked.Token).ConfigureAwait(false);

                    var selectedProjectIds = seed.Models.Values
                        .Where(model => selectedModelIds.Contains(model.ModelId) && model.ProjectId != Guid.Empty)
                        .Select(model => model.ProjectId)
                        .Distinct()
                        .ToArray();
                    var partSearchTasks = selectedProjectIds
                        .SelectMany(projectId => new[]
                        {
                            FindObjectIdsAsync(TypeNames.ModelPart, "model parts", false, projectId, linked.Token),
                            FindObjectIdsAsync(TypeNames.ModelPartCloudPoint, "point-cloud model parts", false, projectId, linked.Token),
                            FindObjectIdsAsync("bim_modelPart_other3Dmodel", "third-party model parts", false, projectId, linked.Token)
                        })
                        .ToArray();
                    var partSearches = partSearchTasks.Length == 0
                        ? Array.Empty<ObjectSearchResult>()
                        : await Task.WhenAll(partSearchTasks).ConfigureAwait(false);
                    var discoveredPartIds = partSearches
                        .SelectMany(x => x.Ids)
                        .Distinct()
                        .ToArray();
                    await Task.Run(
                        () => EnrichPartsFromSearch(scan, discoveredPartIds, linked.Token),
                        linked.Token).ConfigureAwait(false);
                    foreach (var warning in partSearches.Select(x => x.Warning).Where(x => !string.IsNullOrWhiteSpace(x)))
                        scan.AddWarning(warning);
                    if (!string.IsNullOrWhiteSpace(modelSearch.Warning)) scan.AddWarning(modelSearch.Warning);
                    if (selectedRoots.Length > 0 && selectedProjectIds.Length == 0)
                        scan.AddWarning("Selected BIM models could not be associated with a Pilot project; scoped part search will be retried.");
                    if (scan.MissingSnapshotCount > 0)
                        scan.AddWarning(string.Format(
                            "Pilot object subscriptions omitted {0} snapshots; confirmed catalog data was preserved and the missing objects will be retried.",
                            scan.MissingSnapshotCount));
                    scan.IsPartsComplete = modelSearch.IsComplete &&
                        (selectedRoots.Length == 0 || selectedProjectIds.Length > 0) &&
                        partSearches.All(x => x.IsComplete) &&
                        scan.UnresolvedPartCount == 0 &&
                        scan.MissingSnapshotCount == 0;
                    await Task.Run(
                        () => EnrichModelPaths(scan, selectedRoots, linked.Token),
                        linked.Token).ConfigureAwait(false);
                    MergeScan(scan);
                    _log?.Info(string.Format(
                        "BIM model catalog refreshed: {0} coordination models, {1} model parts, {2} Pilot objects scanned; Pilot search returned {3} model IDs.",
                        scan.Models.Count,
                        scan.PartToModel.Count,
                        scan.ScannedObjects,
                        modelIds.Length));
                    if (scan.Models.Count == 0)
                    {
                        _log?.Warn(string.Format(
                            "No coordination models were found. Search IDs={0}, loaded model objects={1}, observed BIM types: {2}.",
                            modelIds.Length,
                            trueModelRoots.Length,
                            scan.ObservedTypes.Count == 0
                                ? "<none>"
                                : string.Join(", ", scan.ObservedTypes.OrderBy(x => x))));
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        _catalogProgressRunning = false;
                        _catalogProgressDescription = "Обновление каталога отменено";
                        _catalogWarnings = new[] { "Обновление каталога частей было отменено." };
                        _partsCatalogReady.TrySetResult(false);
                    }
                    RaiseCatalogChanged();
                    throw;
                }
                catch (Exception exception)
                {
                    lock (_gate)
                    {
                        _catalogProgressRunning = false;
                        _catalogProgressDescription = "Не удалось обновить каталог BIM-моделей";
                        _catalogWarnings = new[] { exception.Message };
                        _partsCatalogReady.TrySetResult(false);
                    }
                    RaiseCatalogChanged();
                    throw;
                }
                finally
                {
                    _refreshGate.Release();
                }

                StartWarmup();
            }
        }

        public Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync()
        {
            return GetModelsAsync(CancellationToken.None);
        }

        public async Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(CancellationToken cancellationToken)
        {
            CatalogModel[] catalog;
            bool partsCatalogComplete;
            lock (_gate)
            {
                catalog = _models.Values.Select(x => x.Clone()).ToArray();
                partsCatalogComplete = _partsCatalogComplete;
            }

            // Do not call IModelStorageProvider here. GetStorage/Dispose is not
            // a cache-neutral read in Pilot-BIM: repeated model-context calls used
            // to grow the host process even though returned wrappers were disposed.
            // Viewer state is sufficient for this catalog DTO.
            const int batchSize = 20;
            var result = new List<ModelDescriptor>(catalog.Length);
            for (var offset = 0; offset < catalog.Length; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = catalog.Skip(offset).Take(batchSize).ToArray();
                var descriptors = await OnUiAsync(() =>
                {
                    var activeTab = _tabServiceProvider?.GetActiveTabPageId() ?? Guid.Empty;
                    return batch.Select(entry =>
                    {
                        var viewers = SafeGetViewers(entry.ModelId);
                        var activeViewer = viewers.FirstOrDefault(x => x.TabId == activeTab);
                        var viewer = activeViewer ?? viewers.FirstOrDefault();
                        var version = viewer == null ? DateTime.MinValue : viewer.ModelVersion;
                        var isLoaded = viewer != null;
                        return new ModelDescriptor
                        {
                            ModelId = entry.ModelId,
                            ModelKey = entry.ModelId.ToString("D"),
                            DisplayName = entry.DisplayName,
                            ProjectId = entry.ProjectId,
                            ProjectName = entry.ProjectName,
                            ProjectPath = entry.ProjectPath,
                            VersionUtc = version == DateTime.MinValue ? null : version.ToUniversalTime().ToString("O"),
                            ViewerVersionUtc = version == DateTime.MinValue ? null : version.ToUniversalTime().ToString("O"),
                            DataVersionUtc = DataVersion(entry.ModelId),
                            IsActive = activeViewer != null,
                            IsOpen = viewers.Length > 0,
                            IsLoaded = isLoaded,
                            IsStorageReady = string.Equals(entry.StorageState, ServiceStates.Ready, StringComparison.Ordinal),
                            IsSearchReady = string.Equals(entry.SearchState, ServiceStates.Ready, StringComparison.Ordinal),
                            StorageState = entry.StorageState,
                            SearchState = entry.SearchState,
                            LastServiceError = entry.LastServiceError,
                            PartsDiscoveryState = partsCatalogComplete ? "complete" : "partial",
                            PartsCount = entry.Parts.Count,
                            ModelPartIds = entry.Parts.Keys.OrderBy(x => x).ToArray(),
                            ModelParts = entry.Parts.Values
                                .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                                .ThenBy(x => x.ModelPartId)
                                .Select(part => new ModelPartDescriptor
                                {
                                    ModelPartId = part.ModelPartId,
                                    DisplayName = part.DisplayName,
                                    TypeName = part.TypeName,
                                    IsLoaded = viewer != null && SafeIsModelPartLoaded(viewer, part.ModelPartId)
                                }).ToArray()
                        };
                    }).ToArray();
                }).ConfigureAwait(false);
                result.AddRange(descriptors);
            }

            return result
                .OrderByDescending(x => x.IsActive)
                .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        public Task<ModelAccess> GetModelAsync(string targetKey)
        {
            return GetModelAsync(targetKey, CancellationToken.None);
        }

        public Task<ModelAccess> GetModelAsync(string targetKey, CancellationToken cancellationToken)
            => GetModelAsync(targetKey, null, cancellationToken);

        public async Task<ModelAccess> GetModelAsync(string targetKey, DateTime? requestedVersion, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(targetKey, out var targetId))
                throw new BimBridgeException(
                    BimBridgeErrorCodes.InvalidRequest,
                    "TargetModelKey must be a coordination model or model part GUID.");

            var resolved = await ResolveTargetAsync(targetId, cancellationToken).ConfigureAwait(false);
            var storage = await GetReadyStorageAsync(resolved.ModelId, cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await OnUiAsync(() =>
                {
                    var activeTab = _tabServiceProvider?.GetActiveTabPageId() ?? Guid.Empty;
                    var viewers = SafeGetViewers(resolved.ModelId);
                    var viewer = viewers.FirstOrDefault(x => x.TabId == activeTab) ?? viewers.FirstOrDefault();
                    var versions = (storage.GetVersions() ?? Enumerable.Empty<DateTime>()).ToArray();
                    var latest = ModelSnapshotPolicy.Select(versions, null);
                    return new { Version = ModelSnapshotPolicy.Select(versions, requestedVersion), Latest = latest,
                        Viewer = viewer == null ? (DateTime?)null : viewer.ModelVersion };
                }).ConfigureAwait(false);
                lock (_gate) _dataVersions[resolved.ModelId] = snapshot.Latest;
                _log?.Info($"Snapshot selected: model={resolved.ModelId:D}; version={snapshot.Version.ToUniversalTime():O}; latest={snapshot.Latest.ToUniversalTime():O}; viewer={snapshot.Viewer?.ToUniversalTime():O}.");
                return new ModelAccess(resolved.ModelId, snapshot.Version, storage, resolved.TargetParts,
                    value => ReleaseStorage(value), f => OnUiAsync(f), snapshot.Latest, snapshot.Viewer,
                    (f, token) => OnUiAsync(f, token), value => _sdkGateway.QueueCleanup(value.Dispose, null, "Late SDK read result Dispose"));
            }
            catch
            {
                ReleaseStorage(storage);
                throw;
            }
        }

        public async Task<SearchTargetAccess> GetSearchTargetAsync(
            string targetKey,
            CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(targetKey, out var targetId))
                throw new BimBridgeException(
                    BimBridgeErrorCodes.InvalidRequest,
                    "TargetModelKey must be a coordination model or model part GUID.");
            var resolved = await ResolveTargetAsync(targetId, cancellationToken).ConfigureAwait(false);
            return new SearchTargetAccess(resolved.ModelId, resolved.TargetParts);
        }

        public Guid[] GetKnownModelPartIds(Guid modelId)
        {
            lock (_gate)
            {
                if (!_models.TryGetValue(modelId, out var model)) return Array.Empty<Guid>();
                return model.Parts.Keys.Where(x => x != Guid.Empty).OrderBy(x => x).ToArray();
            }
        }

        public void LogReadSummary(string operation, ModelAccess access, IEnumerable<Guid> parts, int requested, int failed, int empty = 0, int filtered = 0)
        {
            _log?.Info($"Read summary: operation={operation}; model={access.ModelId:D}; parts={string.Join(",", parts.Distinct())}; version={access.ModelVersionUtc}; requested={requested}; failed={failed}; empty={empty}; filtered={filtered}.");
        }

        public async Task<SearchServiceLease> GetSearchServiceAsync(
            SearchTargetAccess access,
            Guid requestedPartId,
            CancellationToken cancellationToken)
        {
            if (_searchManager == null)
                throw ModelUnavailable(access.ModelId, "BIM search service is unavailable.");
            if (requestedPartId == Guid.Empty)
                throw new BimBridgeException(
                    BimBridgeErrorCodes.InvalidRequest,
                    "A concrete model part is required for bounded search.");
            var parts = new[] { requestedPartId };
            await ResolveModelPartsAsync(
                access.ModelId,
                parts,
                true,
                cancellationToken).ConfigureAwait(false);
            ValidateParts(access.ModelId, parts);

            await _searchInitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            IModelSearchService service = null;
            var registered = false;
            var transferred = false;
            try
            {
                var indexSnapshot = _searchIndexInventory.Capture();
                var hasReadyIndex = indexSnapshot.ContainsAll(parts);
                _log?.Info($"Search initialization requested: model={access.ModelId:D}; part={requestedPartId:D}; readyIndex={hasReadyIndex}; deadlineSeconds={_settings.SearchServiceInitializationTimeoutSeconds}.");
                EnsureSearchResourcesAvailable(
                    hasReadyIndex
                        ? SearchResourceRequestKind.ExistingIndex
                        : SearchResourceRequestKind.NewIndex);
                lock (_gate) SetServiceStateLocked(access.ModelId, null, ServiceStates.Loading, null);
                RaiseCatalogChanged();
                service = await _sdkGateway.RunAsync(
                    () => _searchManager.GetModelPartsSearchServiceAsync(parts),
                    $"Search initialization model={access.ModelId:D}; part={requestedPartId:D}",
                    _settings.SearchServiceInitializationTimeoutSeconds, cancellationToken,
                    late => _sdkGateway.DisposeOnOwnerAsync(() => late?.Dispose())).ConfigureAwait(false);
                if (service == null) throw new InvalidOperationException("IModelSearchManager returned null.");
                lock (_gate)
                {
                    _activeSearchServices++;
                    registered = true;
                    SetServiceStateLocked(access.ModelId, null, ServiceStates.Ready, null);
                }
                RaiseCatalogChanged();
                var lease = new SearchServiceLease(service, x => ReleaseSearchService(access.ModelId, x));
                transferred = true;
                return lease;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (BimBridgeException)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetSearchError(access.ModelId, exception);
                throw ModelUnavailable(access.ModelId, "Unable to initialize model-part search.", exception);
            }
            finally
            {
                if (!transferred && service != null)
                {
                    if (registered) ReleaseSearchService(access.ModelId, service);
                    else DisposeSearchServiceUntracked(service);
                }
                _searchInitializationGate.Release();
            }
        }

        public bool IsPartInScope(Guid modelId, IReadOnlyCollection<Guid> targetParts, Guid partId)
        {
            lock (_gate)
            {
                if (!_partToModel.TryGetValue(partId, out var owner) || owner != modelId)
                    return false;
            }
            return targetParts == null || targetParts.Count == 0 || targetParts.Contains(partId);
        }

        public async Task ResolveModelPartsAsync(
            Guid modelId,
            IEnumerable<Guid> partIds,
            bool throwOnMissing,
            CancellationToken cancellationToken)
        {
            foreach (var partId in (partIds ?? Enumerable.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_partToModel.TryGetValue(partId, out var existingOwner))
                    {
                        if (existingOwner == modelId) continue;
                        if (throwOnMissing)
                            throw new BimBridgeException(
                                BimBridgeErrorCodes.InvalidRequest,
                                string.Format(
                                    "Model part {0:D} belongs to another coordination model.",
                                    partId));
                        continue;
                    }
                }

                try
                {
                    var resolved = await ResolveTargetAsync(partId, cancellationToken).ConfigureAwait(false);
                    if (resolved.ModelId != modelId || !resolved.TargetParts.Contains(partId))
                    {
                        if (throwOnMissing)
                            throw new BimBridgeException(
                                BimBridgeErrorCodes.InvalidRequest,
                                string.Format(
                                    "Model part {0:D} does not belong to coordination model {1:D}.",
                                    partId,
                                    modelId));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch when (!throwOnMissing)
                {
                    // Property loading reports unresolved parts per element.
                }
            }
        }

        private async Task InitializeCatalogAsync(CancellationToken cancellationToken)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                _log?.Info("Initial BIM catalog discovery completed. Automatic full Pilot searches are disabled; model events are refreshed selectively and a full refresh is available from the UI.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _log?.Error("Initial BIM model catalog refresh failed.", exception);
            }
        }

        private async Task<ObjectSearchResult> FindObjectIdsAsync(
            string typeName,
            string description,
            bool required,
            Guid? contextId,
            CancellationToken cancellationToken)
        {
            if (_pilotSearchService == null)
            {
                var unavailable = "Pilot ISearchService is unavailable for " + description + ".";
                if (required)
                    throw new BimBridgeException(BimBridgeErrorCodes.ModelNotAvailable, unavailable, true);
                return new ObjectSearchResult(Array.Empty<Guid>(), false, unavailable);
            }

            await _catalogSearchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var accumulator = new CatalogSearchAccumulator();
                var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Exception searchError = null;
                var streamCompleted = false;
                IDisposable subscription = null;
                var observer = new SearchResultObserver(
                    result =>
                    {
                        if (result == null) return;
                        accumulator.Apply(
                            result.Result,
                            Convert.ToInt64(result.Total),
                            result.Kind == SearchResultKind.Remote);
                        if (accumulator.HasExpectedResult(CatalogSearchMaxResults))
                            received.TrySetResult(true);
                    },
                    error =>
                    {
                        searchError = error;
                        received.TrySetResult(false);
                    },
                    () =>
                    {
                        streamCompleted = true;
                        received.TrySetResult(true);
                    });

                try
                {
                    await OnUiAsync(() =>
                    {
                        var query = _pilotSearchService
                            .GetObjectQueryBuilder()
                            .WithTypeFilter(typeName)
                            .MaxResults(CatalogSearchMaxResults);
                        if (contextId.HasValue && contextId.Value != Guid.Empty)
                            query = query.InContext(contextId.Value);
                        subscription = _pilotSearchService.Search(query).Subscribe(observer);
                        return true;
                    }).ConfigureAwait(false);
                    var timeout = Task.Delay(CatalogSearchTimeout, cancellationToken);
                    var completed = await Task.WhenAny(received.Task, timeout).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var warning = string.Empty;
                    if (completed != received.Task)
                    {
                        warning = "Pilot search for " + description + FormatSearchContext(contextId) +
                            " timed out; partial results were preserved.";
                        _log?.Warn(warning);
                    }
                    if (searchError != null)
                        throw searchError;

                    var resultIds = accumulator.GetIds();
                    var resultTotal = accumulator.Total;
                    var isComplete = accumulator.IsComplete(CatalogSearchMaxResults);
                    if (string.IsNullOrWhiteSpace(warning) && !isComplete)
                    {
                        warning = resultTotal > CatalogSearchMaxResults
                            ? "Pilot search for " + description + FormatSearchContext(contextId) + " was truncated at " + CatalogSearchMaxResults + " objects."
                            : "Pilot search for " + description + FormatSearchContext(contextId) +
                              (streamCompleted ? " completed without a complete remote result." : " returned only a partial result.");
                        _log?.Warn(warning);
                    }
                    return new ObjectSearchResult(resultIds, isComplete, warning);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    var message = "Pilot search failed while discovering " + description + FormatSearchContext(contextId) + ".";
                    if (!required)
                    {
                        _log?.Warn(message + " " + exception.Message);
                        return new ObjectSearchResult(accumulator.GetIds(), false, message);
                    }
                    throw new BimBridgeException(
                        BimBridgeErrorCodes.ModelNotAvailable,
                        message,
                        true,
                        exception);
                }
                finally
                {
                    subscription?.Dispose();
                }
            }
            finally
            {
                _catalogSearchGate.Release();
            }
        }

        private static string FormatSearchContext(Guid? contextId) =>
            contextId.HasValue && contextId.Value != Guid.Empty
                ? " in project " + contextId.Value.ToString("D")
                : string.Empty;

        private CatalogScan ScanTree(
            IEnumerable<PilotObjectSnapshot> roots,
            CancellationToken cancellationToken)
        {
            var result = new CatalogScan();
            if (_objectsRepository == null || roots == null) return result;

            var visited = new HashSet<Guid>();
            var pending = new Queue<PendingObject>();
            var initial = roots
                .Where(x => x != null && x.Id != Guid.Empty)
                .GroupBy(x => x.Id)
                .ToDictionary(x => x.Key, x => x.First());
            foreach (var root in initial.Values)
                pending.Enqueue(new PendingObject(
                    root.Id,
                    BimCatalogIdentityRules.GetRootOwner(root.Id, root.TypeName)));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new List<PendingObject>();
                while (pending.Count > 0 && batch.Count < SubscribeChunkSize)
                {
                    var item = pending.Dequeue();
                    if (item.Id != Guid.Empty && visited.Add(item.Id)) batch.Add(item);
                }
                if (batch.Count == 0) continue;

                var snapshots = new Dictionary<Guid, PilotObjectSnapshot>();
                var missing = new List<Guid>();
                foreach (var item in batch)
                {
                    if (initial.TryGetValue(item.Id, out var snapshot)) snapshots[item.Id] = snapshot;
                    else missing.Add(item.Id);
                }
                foreach (var snapshot in LoadSnapshots(missing, cancellationToken))
                    snapshots[snapshot.Id] = snapshot;

                foreach (var item in batch)
                {
                    if (!snapshots.TryGetValue(item.Id, out var snapshot))
                    {
                        result.AddMissingSnapshots(1);
                        continue;
                    }
                    if (snapshot.IsDeleted || snapshot.IsInRecycleBin) continue;
                    result.RegisterObject(snapshot);
                    var owner = item.OwnerModelId;
                    if (IsCoordinationModel(snapshot.TypeName))
                    {
                        owner = snapshot.Id;
                        result.GetOrAddModel(snapshot.Id, snapshot.DisplayName);
                    }
                    else if (IsModelPart(snapshot.TypeName) && owner.HasValue)
                    {
                        result.AddPart(owner.Value, snapshot);
                    }

                    foreach (var childId in snapshot.ChildIds)
                        pending.Enqueue(new PendingObject(childId, owner));
                }
            }

            return result;
        }

        private void EnrichPartsFromSearch(
            CatalogScan scan,
            IEnumerable<Guid> sourcePartIds,
            CancellationToken cancellationToken)
        {
            if (scan == null) return;
            var partIds = (sourcePartIds ?? Enumerable.Empty<Guid>())
                .Where(x => x != Guid.Empty && !scan.PartToModel.ContainsKey(x))
                .Distinct()
                .ToArray();
            if (partIds.Length == 0) return;

            var cache = new Dictionary<Guid, PilotObjectSnapshot>();
            var active = new Dictionary<Guid, PilotObjectSnapshot>();
            foreach (var part in LoadSnapshots(partIds, cancellationToken))
            {
                if (part == null || part.IsDeleted || part.IsInRecycleBin || !IsModelPart(part.TypeName)) continue;
                cache[part.Id] = part;
                active[part.Id] = part;
                scan.RegisterObject(part);
            }
            foreach (var missing in partIds.Where(x => !active.ContainsKey(x)))
                scan.AddUnresolvedPart(missing);

            for (var depth = 0; active.Count > 0 && depth < 128; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parentIds = active.Values
                    .Select(x => x.ParentId)
                    .Where(x => x != Guid.Empty && !cache.ContainsKey(x))
                    .Distinct()
                    .ToArray();
                foreach (var parent in LoadSnapshots(parentIds, cancellationToken))
                {
                    if (parent == null) continue;
                    cache[parent.Id] = parent;
                    scan.RegisterObject(parent);
                }

                var next = new Dictionary<Guid, PilotObjectSnapshot>();
                foreach (var pair in active)
                {
                    var part = pair.Value;
                    if (part.ParentId == Guid.Empty || !cache.TryGetValue(part.ParentId, out var parent))
                    {
                        scan.AddUnresolvedPart(pair.Key);
                        continue;
                    }
                    if (IsCoordinationModel(parent.TypeName))
                    {
                        scan.GetOrAddModel(parent.Id, parent.DisplayName);
                        scan.AddPart(parent.Id, cache[pair.Key]);
                        continue;
                    }
                    next[pair.Key] = parent;
                }
                active = next;
            }

            foreach (var unresolved in active.Keys)
                scan.AddUnresolvedPart(unresolved);
            if (scan.UnresolvedPartCount > 0)
                scan.AddWarning(string.Format(
                    "Не удалось определить консолидированную модель для {0} частей; разрешение будет повторено при следующем обновлении.",
                    scan.UnresolvedPartCount));
        }

        private void EnrichModelPaths(
            CatalogScan scan,
            IEnumerable<PilotObjectSnapshot> roots,
            CancellationToken cancellationToken)
        {
            var known = (roots ?? Enumerable.Empty<PilotObjectSnapshot>())
                .Concat(scan.Objects.Values)
                .Where(x => x != null)
                .GroupBy(x => x.Id)
                .ToDictionary(x => x.Key, x => x.First());
            var frontier = known.Values
                .Select(x => x.ParentId)
                .Where(x => x != Guid.Empty && !known.ContainsKey(x))
                .Distinct()
                .ToArray();

            for (var depth = 0; frontier.Length > 0 && depth < 128; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestedCount = frontier.Length;
                var loaded = LoadSnapshots(frontier, cancellationToken)
                    .Where(x => x != null)
                    .ToArray();
                if (loaded.Length < requestedCount)
                    scan.AddMissingSnapshots(requestedCount - loaded.Length);
                foreach (var item in loaded) known[item.Id] = item;
                frontier = loaded
                    .Select(x => x.ParentId)
                    .Where(x => x != Guid.Empty && !known.ContainsKey(x))
                    .Distinct()
                    .ToArray();
            }

            foreach (var project in known.Values.Where(x => x.TypeIsProject))
                scan.RegisterProject(project);

            foreach (var model in scan.Models.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!known.TryGetValue(model.ModelId, out var current)) continue;
                var chain = new List<PilotObjectSnapshot>();
                var visited = new HashSet<Guid>();
                while (current != null && visited.Add(current.Id))
                {
                    if (!current.IsDeleted && !current.IsInRecycleBin) chain.Add(current);
                    if (current.ParentId == Guid.Empty ||
                        !known.TryGetValue(current.ParentId, out current)) break;
                }
                chain.Reverse();

                var projectIndex = chain.FindLastIndex(x => x.TypeIsProject);
                if (projectIndex < 0) projectIndex = Math.Max(0, chain.Count - 3);
                var visible = chain
                    .Skip(projectIndex)
                    .Where(x => !string.IsNullOrWhiteSpace(x.DisplayName))
                    .Select(x => x.DisplayName)
                    .ToArray();
                var project = chain.FirstOrDefault(x => x.TypeIsProject);
                model.ProjectId = project?.Id ?? Guid.Empty;
                model.ProjectName = project?.DisplayName ?? "";
                model.ProjectPath = visible.Length == 0
                    ? model.DisplayName
                    : string.Join(" / ", visible);
                if (!string.IsNullOrWhiteSpace(model.ProjectPath))
                    model.DisplayName = model.ProjectPath;
            }
        }

        private IReadOnlyList<PilotObjectSnapshot> LoadSnapshots(
            IEnumerable<Guid> sourceIds,
            CancellationToken cancellationToken)
        {
            var ids = (sourceIds ?? Enumerable.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();
            if (ids.Length == 0) return Array.Empty<PilotObjectSnapshot>();

            var result = new List<PilotObjectSnapshot>();
            for (var offset = 0; offset < ids.Length; offset += SubscribeChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = ids
                    .Skip(offset)
                    .Take(SubscribeChunkSize)
                    .ToArray();
                result.AddRange(LoadSnapshotChunk(chunk, cancellationToken));
            }
            return result;
        }

        private IReadOnlyList<PilotObjectSnapshot> LoadSnapshotChunk(
            Guid[] ids,
            CancellationToken cancellationToken)
        {
            var snapshots = new List<PilotObjectSnapshot>();
            var remaining = new HashSet<Guid>(ids);
            using (var completed = new ManualResetEventSlim(false))
            {
                var observer = new DataObjectObserver(
                    value =>
                    {
                        var snapshot = CopySnapshot(value);
                        if (snapshot == null) return;
                        lock (remaining)
                        {
                            if (!remaining.Remove(snapshot.Id)) return;
                            snapshots.Add(snapshot);
                            if (remaining.Count == 0) completed.Set();
                        }
                    },
                    _ => completed.Set(),
                    () => completed.Set());
                IDisposable subscription = null;
                try
                {
                    subscription = _objectsRepository.SubscribeObjects(ids).Subscribe(observer);
                    completed.Wait(SubscribeTimeout, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    _log?.Warn("Pilot object subscription returned a partial result: " + exception.Message);
                }
                finally
                {
                    subscription?.Dispose();
                }
            }
            return snapshots;
        }

        private async Task<ResolvedTarget> ResolveTargetAsync(Guid targetId, CancellationToken cancellationToken)
        {
            if (TryResolveFromCatalog(targetId, out var resolved)) return resolved;

            var snapshot = LoadSnapshots(new[] { targetId }, cancellationToken).FirstOrDefault();
            if (snapshot != null)
            {
                if (IsCoordinationModel(snapshot.TypeName))
                {
                    AddResolvedModel(snapshot.Id, snapshot.DisplayName, null);
                    return new ResolvedTarget(snapshot.Id, Array.Empty<Guid>());
                }
                if (IsModelPart(snapshot.TypeName))
                {
                    var owner = await FindCoordinationParentAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    if (owner != null)
                    {
                        AddResolvedModel(owner.Id, owner.DisplayName, snapshot);
                        return new ResolvedTarget(owner.Id, new[] { snapshot.Id });
                    }
                }
            }

            try
            {
                var storage = await OnUiAsync(() => _storageProvider?.GetStorage(targetId)).ConfigureAwait(false);
                RegisterStorage(storage);
                try
                {
                    if (storage != null && storage.ModelId == targetId)
                    {
                        AddResolvedModel(targetId, "BIM " + targetId.ToString("D"), null);
                        await MergeStoragePartsAsync(targetId, storage, cancellationToken).ConfigureAwait(false);
                        return new ResolvedTarget(targetId, Array.Empty<Guid>());
                    }
                }
                finally
                {
                    ReleaseStorage(storage);
                }
            }
            catch { }

            // A valid Pilot GUID was already resolved directly above. Do not run
            // a global catalog search for every unknown external GUID: Pilot keeps
            // internal search results beyond subscription disposal and the former
            // fallback caused host-process growth.
            throw ModelUnavailable(targetId, "The GUID was not found as a coordination model or model part.");
        }

        private async Task<PilotObjectSnapshot> FindCoordinationParentAsync(
            PilotObjectSnapshot child,
            CancellationToken cancellationToken)
        {
            var visited = new HashSet<Guid> { child.Id };
            var parentId = child.ParentId;
            for (var depth = 0; parentId != Guid.Empty && depth < 128 && visited.Add(parentId); depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = LoadSnapshots(new[] { parentId }, cancellationToken).FirstOrDefault();
                if (parent == null) return null;
                if (IsCoordinationModel(parent.TypeName)) return parent;
                parentId = parent.ParentId;
            }
            await Task.CompletedTask;
            return null;
        }

        private bool TryResolveFromCatalog(Guid id, out ResolvedTarget resolved)
        {
            lock (_gate)
            {
                if (_partToModel.TryGetValue(id, out var modelId))
                {
                    resolved = new ResolvedTarget(modelId, new[] { id });
                    return true;
                }
                if (_models.TryGetValue(id, out var model) && !model.Parts.ContainsKey(id))
                {
                    resolved = new ResolvedTarget(id, Array.Empty<Guid>());
                    return true;
                }
            }
            resolved = null;
            return false;
        }

        private void MergeScan(CatalogScan scan)
        {
            var filterChanged = false;
            var projectNames = new HashSet<string>(
                _settings.GetLoadedProjectNames(),
                StringComparer.CurrentCultureIgnoreCase);
            var filterKey = string.Join("\u001f", projectNames.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
            var selectedModels = scan.Models.Values
                .Where(model => IsSelectedProject(model, projectNames))
                .ToArray();
            var selectedModelIds = new HashSet<Guid>(selectedModels.Select(x => x.ModelId));
            var selectedProjectIds = new HashSet<Guid>(
                selectedModels.Where(x => x.ProjectId != Guid.Empty).Select(x => x.ProjectId));

            lock (_gate)
            {
                var oldModels = _models.ToDictionary(x => x.Key, x => x.Value);
                if (!string.Equals(_loadedProjectFilterKey, filterKey, StringComparison.Ordinal))
                {
                    filterChanged = true;
                    _loadedProjectFilterKey = filterKey;
                    _warmupGeneration++;
                    _warmupAttempted.Clear();
                }
                else
                {
                    var retainedModelIds = scan.IsPartsComplete
                        ? selectedModelIds
                        : new HashSet<Guid>(selectedModelIds.Concat(oldModels.Keys));
                    // Failed/on-demand probes remain attempted. GetStorage may
                    // populate caches owned by the Pilot host even after Dispose;
                    // retrying every minute was the measured long-term leak.
                    _warmupAttempted.IntersectWith(retainedModelIds);
                }

                // A complete scan replaces the selected catalog.  A partial scan
                // only enriches it so heartbeat routing never loses previously
                // confirmed model keys while Pilot subscriptions are incomplete.
                if (filterChanged || scan.IsPartsComplete)
                {
                    _models.Clear();
                    _partToModel.Clear();
                    _projects.Clear();
                }
                foreach (var project in scan.Projects.Values.Where(x => selectedProjectIds.Contains(x.Id)))
                    _projects[project.Id] = new CatalogProject(project.Id, project.ParentId, project.DisplayName);
                foreach (var scanned in selectedModels)
                {
                    if (oldModels.TryGetValue(scanned.ModelId, out var previous))
                    {
                        scanned.StorageState = previous.StorageState;
                        scanned.SearchState = filterChanged ? ServiceStates.OnDemand : previous.SearchState;
                        scanned.LastServiceError = previous.LastServiceError;
                        if (!scan.IsPartsComplete)
                        {
                            foreach (var previousPart in previous.Parts)
                                if (!scanned.Parts.ContainsKey(previousPart.Key))
                                    scanned.Parts[previousPart.Key] = previousPart.Value;
                        }
                    }
                    _models[scanned.ModelId] = scanned;
                    foreach (var part in scanned.Parts.Values)
                    {
                        if (part.ModelPartId != scanned.ModelId)
                            _models.Remove(part.ModelPartId);
                        _partToModel[part.ModelPartId] = scanned.ModelId;
                    }
                }
                _partsCatalogComplete = scan.IsPartsComplete;
                _unresolvedPartCount = scan.UnresolvedPartCount;
                _catalogWarnings = scan.Warnings.ToArray();
                _partsCatalogReady.TrySetResult(_partsCatalogComplete);
            }
            _log?.Info(string.Format(
                "BIM model project filter selected {0} of {1} models for: {2}.",
                selectedModels.Length,
                scan.Models.Count,
                string.Join("; ", projectNames.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))));
            if (selectedModels.Length == 0)
                _log?.Warn("No BIM models matched the configured project names. Check LoadedProjectNames in BIM Bridge settings.");
            RaiseCatalogChanged();
        }

        private static bool IsSelectedProject(CatalogModel model, HashSet<string> projectNames)
        {
            if (model == null || projectNames == null || projectNames.Count == 0) return false;
            if (!string.IsNullOrWhiteSpace(model.ProjectName) && projectNames.Contains(model.ProjectName.Trim()))
                return true;
            return (model.ProjectPath ?? "")
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Any(projectNames.Contains);
        }

        private void AddResolvedModel(Guid modelId, string displayName, PilotObjectSnapshot part)
        {
            if (part != null && part.Id == modelId) return;
            lock (_gate)
            {
                if (!_models.TryGetValue(modelId, out var model))
                {
                    model = new CatalogModel(modelId, displayName);
                    _models[modelId] = model;
                }
                if (part != null)
                {
                    _models.Remove(part.Id);
                    model.Parts[part.Id] = new CatalogPart(part.Id, part.DisplayName, part.TypeName);
                    _partToModel[part.Id] = modelId;
                }
            }
            RaiseCatalogChanged();
        }

        private async Task MergeStoragePartsAsync(
            Guid modelId,
            IModelStorage storage,
            CancellationToken cancellationToken)
        {
            Guid[] parts;
            try
            {
                parts = await OnUiAsync(() => storage.GetModelPartsIds()?.Where(x => x != Guid.Empty).Distinct().ToArray() ?? Array.Empty<Guid>()).ConfigureAwait(false);
                var latest = await OnUiAsync(() => SafeGetLatestVersion(storage)).ConfigureAwait(false);
                if (latest != DateTime.MinValue) lock (_gate) _dataVersions[modelId] = latest;
            }
            catch { parts = Array.Empty<Guid>(); }
            var snapshots = await Task.Run(
                () => LoadSnapshots(parts, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var names = snapshots.ToDictionary(x => x.Id, x => x);
            lock (_gate)
            {
                if (!_models.TryGetValue(modelId, out var model))
                {
                    model = new CatalogModel(modelId, "BIM " + modelId.ToString("D"));
                    _models[modelId] = model;
                }
                foreach (var partId in parts)
                {
                    if (partId == modelId) continue;
                    _models.Remove(partId);
                    if (names.TryGetValue(partId, out var snapshot))
                        model.Parts[partId] =
                            new CatalogPart(partId, snapshot.DisplayName, snapshot.TypeName);
                    else if (!model.Parts.ContainsKey(partId))
                        model.Parts[partId] =
                            new CatalogPart(partId, partId.ToString("D"), TypeNames.ModelPart);
                    _partToModel[partId] = modelId;
                }
            }
            RaiseCatalogChanged();
        }

        private void ValidateParts(Guid modelId, IEnumerable<Guid> parts)
        {
            foreach (var partId in parts)
            {
                lock (_gate)
                {
                    if (!_partToModel.TryGetValue(partId, out var owner) || owner != modelId)
                        throw new BimBridgeException(
                            BimBridgeErrorCodes.InvalidRequest,
                            string.Format("Model part {0:D} does not belong to coordination model {1:D}.", partId, modelId));
                }
            }
        }

        private void SetSearchError(Guid modelId, Exception exception)
        {
            lock (_gate) SetServiceStateLocked(modelId, null, ServiceStates.Error, exception.Message);
            RaiseCatalogChanged();
        }

        private void SetServiceStateLocked(Guid modelId, string storageState, string searchState, string error)
        {
            if (!_models.TryGetValue(modelId, out var model)) return;
            if (storageState != null) model.StorageState = storageState;
            if (searchState != null) model.SearchState = searchState;
            model.LastServiceError = error;
        }

        private void OnModelChanged(object sender, ModelEventArgs args)
        {
            if (args?.Viewer != null)
            {
                lock (_gate)
                {
                    if (_models.TryGetValue(args.Viewer.ModelId, out var known))
                    {
                        known.StorageState = ServiceStates.Ready;
                        known.LastServiceError = null;
                    }
                }
                lock (_modelEventGate)
                {
                    _pendingModelEventIds.Add(args.Viewer.ModelId);
                }
                RaiseCatalogChanged();
                _modelEventSignal.Request();
            }
        }

        private async Task ProcessModelEventsAsync(CancellationToken cancellationToken)
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Guid[] modelIds;
                lock (_modelEventGate)
                {
                    modelIds = _pendingModelEventIds.ToArray();
                    _pendingModelEventIds.Clear();
                }
                foreach (var modelId in modelIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { await RefreshKnownModelAsync(modelId, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                    catch (Exception exception)
                    {
                        _log?.Warn("Selective BIM model refresh failed for " + modelId.ToString("D") + ": " + exception.Message);
                    }
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        private async Task RefreshKnownModelAsync(Guid modelId, CancellationToken cancellationToken)
        {
            var roots = await Task.Run(
                () => LoadSnapshots(new[] { modelId }, cancellationToken)
                    .Where(x => x != null && !x.IsDeleted && !x.IsInRecycleBin && IsCoordinationModel(x.TypeName))
                    .ToArray(),
                cancellationToken).ConfigureAwait(false);
            if (roots.Length == 0) return;

            // A model-open/load/close event is not evidence that the object tree
            // changed. Refresh only the model and its ancestor metadata. Walking
            // every child here made UI event bursts create thousands of Pilot
            // subscriptions and recreated the same long-lived host cache problem.
            var scan = new CatalogScan();
            scan.RegisterObject(roots[0]);
            scan.GetOrAddModel(roots[0].Id, roots[0].DisplayName);
            await Task.Run(() => EnrichModelPaths(scan, roots, cancellationToken), cancellationToken).ConfigureAwait(false);
            var selectedNames = new HashSet<string>(_settings.GetLoadedProjectNames(), StringComparer.CurrentCultureIgnoreCase);
            var model = scan.Models.Values.FirstOrDefault(x => x.ModelId == modelId);
            if (model == null || !IsSelectedProject(model, selectedNames)) return;

            MergeTargetedModel(model, scan.Projects.Values);
            Interlocked.Increment(ref _selectiveCatalogRefreshCount);
            _log?.Info(string.Format(
                "Selective BIM model metadata refresh completed: {0:D}, {1} retained parts. No subtree walk, global Pilot search or storage probe was used.",
                modelId,
                model.Parts.Count));
        }

        private void MergeTargetedModel(CatalogModel scanned, IEnumerable<PilotObjectSnapshot> projects)
        {
            lock (_gate)
            {
                if (_models.TryGetValue(scanned.ModelId, out var previous))
                {
                    scanned.StorageState = previous.StorageState;
                    scanned.SearchState = previous.SearchState;
                    scanned.LastServiceError = previous.LastServiceError;
                    foreach (var part in previous.Parts)
                        if (!scanned.Parts.ContainsKey(part.Key)) scanned.Parts[part.Key] = part.Value;
                }
                _models[scanned.ModelId] = scanned;
                foreach (var project in projects ?? Enumerable.Empty<PilotObjectSnapshot>())
                    _projects[project.Id] = new CatalogProject(project.Id, project.ParentId, project.DisplayName);
                foreach (var part in scanned.Parts.Values)
                {
                    if (part.ModelPartId != scanned.ModelId) _models.Remove(part.ModelPartId);
                    _partToModel[part.ModelPartId] = scanned.ModelId;
                }
            }
            RaiseCatalogChanged();
        }

        private void RaiseCatalogChanged()
        {
            _catalogChangedSignal.Request();
        }

        private IModelViewer[] SafeGetViewers(Guid modelId)
        {
            try { return _modelManager?.GetViewers(modelId)?.Where(x => x != null).ToArray() ?? Array.Empty<IModelViewer>(); }
            catch { return Array.Empty<IModelViewer>(); }
        }

        private static bool SafeIsModelPartLoaded(IModelViewer viewer, Guid partId)
        {
            try { return viewer.IsModelPartLoaded(partId); }
            catch { return false; }
        }

        private static DateTime SafeGetLatestVersion(IModelStorage storage)
        {
            try { return storage?.GetVersions()?.OrderByDescending(x => x).FirstOrDefault() ?? DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private static PilotObjectSnapshot CopySnapshot(PilotDataObject value)
        {
            if (value == null) return null;
            try
            {
                var id = value.Id;
                if (id == Guid.Empty) return null;
                Guid parentId;
                string displayName;
                string typeName;
                string typeTitle;
                bool typeIsProject;
                bool isDeleted;
                bool isInRecycleBin;
                Guid[] children;
                try { parentId = value.ParentId; }
                catch { parentId = Guid.Empty; }
                try { displayName = value.DisplayName; }
                catch { displayName = ""; }
                try { typeName = value.Type?.Name ?? ""; }
                catch { typeName = ""; }
                try { typeTitle = value.Type?.Title ?? ""; }
                catch { typeTitle = ""; }
                try { typeIsProject = value.Type?.IsProject == true; }
                catch { typeIsProject = false; }
                try { isDeleted = value.IsDeleted; }
                catch { isDeleted = false; }
                try { isInRecycleBin = value.IsInRecycleBin; }
                catch { isInRecycleBin = false; }
                try
                {
                    children = value.Children?
                        .Where(x => x != Guid.Empty)
                        .Distinct()
                        .ToArray() ?? Array.Empty<Guid>();
                }
                catch { children = Array.Empty<Guid>(); }
                return new PilotObjectSnapshot(
                    id,
                    parentId,
                    string.IsNullOrWhiteSpace(displayName) ? id.ToString("D") : displayName,
                    typeName,
                    typeTitle,
                    typeIsProject,
                    isDeleted,
                    isInRecycleBin,
                    children);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsCoordinationModel(string typeName) =>
            BimCatalogIdentityRules.IsExactCoordinationModel(typeName);

        private static bool IsModelPart(string typeName) =>
            BimCatalogIdentityRules.IsSupportedModelPart(typeName);

        private static BimBridgeException ModelUnavailable(Guid id, string details, Exception inner = null) =>
            new BimBridgeException(
                BimBridgeErrorCodes.ModelNotAvailable,
                string.Format("Model or model part {0:D} is not available. {1}", id, details),
                true,
                inner);

        private Task<T> OnUiAsync<T>(Func<T> action)
            => OnUiAsync(action, _lifetime.Token);

        private Task<T> OnUiAsync<T>(Func<T> action, CancellationToken token)
        {
            return _sdkGateway.RunAsync(action, "SDK snapshot/read", token,
                value => _sdkGateway.DisposeOnOwnerAsync(() =>
                {
                    if (value is IDisposable disposable) disposable.Dispose();
                }));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ModelContextProvider));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pilotUi.Dispose();
            if (_modelManager != null)
            {
                _modelManager.ModelOpened -= OnModelChanged;
                _modelManager.ModelLoaded -= OnModelChanged;
                _modelManager.ModelClosed -= OnModelChanged;
            }
            _lifetime.Cancel();
            _catalogChangedSignal.Dispose();
            _modelEventSignal.Dispose();
        }

        private static class ServiceStates
        {
            public const string OnDemand = "on-demand";
            public const string Pending = "pending";
            public const string Loading = "loading";
            public const string Ready = "ready";
            public const string Error = "error";
        }

        private sealed class CatalogModel
        {
            public CatalogModel(Guid modelId, string displayName)
            {
                ModelId = modelId;
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? modelId.ToString("D") : displayName;
            }

            public Guid ModelId;
            public string DisplayName;
            public Guid ProjectId;
            public string ProjectName;
            public string ProjectPath;
            public string StorageState = ServiceStates.Pending;
            public string SearchState = ServiceStates.Pending;
            public string LastServiceError;
            public Dictionary<Guid, CatalogPart> Parts = new Dictionary<Guid, CatalogPart>();

            public CatalogModel Clone()
            {
                var clone = new CatalogModel(ModelId, DisplayName)
                {
                    StorageState = StorageState,
                    SearchState = SearchState,
                    LastServiceError = LastServiceError,
                    ProjectId = ProjectId,
                    ProjectName = ProjectName,
                    ProjectPath = ProjectPath
                };
                foreach (var pair in Parts) clone.Parts[pair.Key] = pair.Value;
                return clone;
            }
        }

        private sealed class CatalogProject
        {
            public CatalogProject(Guid projectId, Guid parentId, string displayName)
            {
                ProjectId = projectId;
                ParentId = parentId;
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? projectId.ToString("D") : displayName;
            }

            public Guid ProjectId { get; }
            public Guid ParentId { get; }
            public string DisplayName { get; }
        }

        private sealed class CatalogPart
        {
            public CatalogPart(Guid modelPartId, string displayName, string typeName)
            {
                ModelPartId = modelPartId;
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? modelPartId.ToString("D") : displayName;
                TypeName = typeName ?? "";
            }
            public Guid ModelPartId;
            public string DisplayName;
            public string TypeName;
        }

        private sealed class CatalogScan
        {
            private readonly HashSet<Guid> _unresolvedParts = new HashSet<Guid>();
            public Dictionary<Guid, CatalogModel> Models { get; } = new Dictionary<Guid, CatalogModel>();
            public Dictionary<Guid, PilotObjectSnapshot> Projects { get; } = new Dictionary<Guid, PilotObjectSnapshot>();
            public Dictionary<Guid, PilotObjectSnapshot> Objects { get; } = new Dictionary<Guid, PilotObjectSnapshot>();
            public Dictionary<Guid, Guid> PartToModel { get; } = new Dictionary<Guid, Guid>();
            public List<string> Warnings { get; } = new List<string>();
            public HashSet<string> ObservedTypes { get; } =
                new HashSet<string>(StringComparer.Ordinal);
            public int ScannedObjects { get; private set; }
            public int MissingSnapshotCount { get; private set; }
            public bool IsPartsComplete { get; set; }
            public int UnresolvedPartCount => _unresolvedParts.Count;

            public void RegisterObject(PilotObjectSnapshot snapshot)
            {
                ScannedObjects++;
                Objects[snapshot.Id] = snapshot;
                if (snapshot.TypeIsProject) Projects[snapshot.Id] = snapshot;
                if (!string.IsNullOrWhiteSpace(snapshot.TypeName) &&
                    snapshot.TypeName.IndexOf("bim", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ObservedTypes.Add(snapshot.TypeName);
                }
            }

            public CatalogModel GetOrAddModel(Guid id, string name)
            {
                if (!Models.TryGetValue(id, out var model))
                {
                    model = new CatalogModel(id, name);
                    Models[id] = model;
                }
                return model;
            }

            public void AddPart(Guid modelId, PilotObjectSnapshot snapshot)
            {
                if (snapshot == null || snapshot.Id == Guid.Empty || snapshot.Id == modelId ||
                    !Models.TryGetValue(modelId, out var model))
                {
                    if (snapshot != null) AddUnresolvedPart(snapshot.Id);
                    return;
                }
                Models.Remove(snapshot.Id);
                model.Parts[snapshot.Id] =
                    new CatalogPart(snapshot.Id, snapshot.DisplayName, snapshot.TypeName);
                PartToModel[snapshot.Id] = modelId;
                _unresolvedParts.Remove(snapshot.Id);
            }

            public void AddUnresolvedPart(Guid partId)
            {
                if (partId != Guid.Empty && !PartToModel.ContainsKey(partId)) _unresolvedParts.Add(partId);
            }

            public void AddWarning(string warning)
            {
                if (!string.IsNullOrWhiteSpace(warning) && !Warnings.Contains(warning)) Warnings.Add(warning);
            }

            public void AddMissingSnapshots(int count)
            {
                if (count > 0) MissingSnapshotCount += count;
            }

            public void RegisterProject(PilotObjectSnapshot snapshot)
            {
                if (snapshot != null && snapshot.TypeIsProject)
                    Projects[snapshot.Id] = snapshot;
            }
        }

        private sealed class ObjectSearchResult
        {
            public ObjectSearchResult(Guid[] ids, bool isComplete, string warning)
            {
                Ids = ids ?? Array.Empty<Guid>();
                IsComplete = isComplete;
                Warning = warning ?? string.Empty;
            }

            public Guid[] Ids { get; }
            public bool IsComplete { get; }
            public string Warning { get; }
        }

        private sealed class PilotObjectSnapshot
        {
            public PilotObjectSnapshot(
                Guid id,
                Guid parentId,
                string displayName,
                string typeName,
                string typeTitle,
                bool typeIsProject,
                bool isDeleted,
                bool isInRecycleBin,
                Guid[] childIds)
            {
                Id = id;
                ParentId = parentId;
                DisplayName = displayName;
                TypeName = typeName;
                TypeTitle = typeTitle;
                TypeIsProject = typeIsProject;
                IsDeleted = isDeleted;
                IsInRecycleBin = isInRecycleBin;
                ChildIds = childIds ?? Array.Empty<Guid>();
            }
            public Guid Id { get; }
            public Guid ParentId { get; }
            public string DisplayName { get; }
            public string TypeName { get; }
            public string TypeTitle { get; }
            public bool TypeIsProject { get; }
            public bool IsDeleted { get; }
            public bool IsInRecycleBin { get; }
            public Guid[] ChildIds { get; }
        }

        private sealed class PendingObject
        {
            public PendingObject(Guid id, Guid? ownerModelId)
            {
                Id = id;
                OwnerModelId = ownerModelId;
            }
            public Guid Id { get; }
            public Guid? OwnerModelId { get; }
        }

        private sealed class ResolvedTarget
        {
            public ResolvedTarget(Guid modelId, IReadOnlyCollection<Guid> targetParts)
            {
                ModelId = modelId;
                TargetParts = targetParts ?? Array.Empty<Guid>();
            }
            public Guid ModelId { get; }
            public IReadOnlyCollection<Guid> TargetParts { get; }
        }

        private sealed class DataObjectObserver : IObserver<PilotDataObject>
        {
            private readonly Action<PilotDataObject> _next;
            private readonly Action<Exception> _error;
            private readonly Action _completed;

            public DataObjectObserver(
                Action<PilotDataObject> next,
                Action<Exception> error,
                Action completed)
            {
                _next = next;
                _error = error;
                _completed = completed;
            }

            public void OnNext(PilotDataObject value) => _next(value);
            public void OnError(Exception error) => _error(error);
            public void OnCompleted() => _completed();
        }

        private sealed class SearchResultObserver : IObserver<ISearchResult>
        {
            private readonly Action<ISearchResult> _next;
            private readonly Action<Exception> _error;
            private readonly Action _completed;

            public SearchResultObserver(
                Action<ISearchResult> next,
                Action<Exception> error,
                Action completed)
            {
                _next = next;
                _error = error;
                _completed = completed;
            }

            public void OnNext(ISearchResult value) => _next(value);
            public void OnError(Exception error) => _error(error);
            public void OnCompleted() => _completed();
        }
    }
}
