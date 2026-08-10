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
using System.IO;
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
        public bool IsActive { get; set; }
        public bool IsOpen { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsStorageReady { get; set; }
        public bool IsSearchReady { get; set; }
        public string StorageState { get; set; }
        public string SearchState { get; set; }
        public string LastServiceError { get; set; }
        public Guid[] ModelPartIds { get; set; }
        public ModelPartDescriptor[] ModelParts { get; set; }
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

    public sealed class ModelAccess
    {
        public ModelAccess(
            Guid modelId,
            DateTime version,
            IModelStorage storage,
            IReadOnlyCollection<Guid> targetModelPartIds)
        {
            ModelId = modelId;
            Version = version;
            Storage = storage;
            TargetModelPartIds = targetModelPartIds ?? Array.Empty<Guid>();
        }

        public Guid ModelId { get; }
        public DateTime Version { get; }
        public IModelStorage Storage { get; }
        public IReadOnlyCollection<Guid> TargetModelPartIds { get; }
    }

    public sealed class ModelContextProvider : IDisposable
    {
        private const int SubscribeChunkSize = 200;
        private const int WarmupParallelism = 4;
        private static readonly TimeSpan SubscribeTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
        // A cold storage can download its index for longer than five seconds.
        private static readonly TimeSpan StorageLoadTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SearchServiceLoadTimeout = TimeSpan.FromSeconds(30);

        private readonly object _gate = new object();
        private readonly IObjectsRepository _objectsRepository;
        private readonly ISearchService _pilotSearchService;
        private readonly IModelManager _modelManager;
        private readonly IModelStorageProvider _storageProvider;
        private readonly IModelSearchManager _searchManager;
        private readonly ITabServiceProvider _tabServiceProvider;
        private readonly BimBridgeSettings _settings;
        private readonly IBimBridgeLog _log;
        private readonly Dispatcher _uiDispatcher;
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _warmupGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _searchInitializationGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly CoalescingSignal _catalogChangedSignal;
        private readonly Dictionary<Guid, CatalogModel> _models = new Dictionary<Guid, CatalogModel>();
        private readonly Dictionary<Guid, CatalogProject> _projects = new Dictionary<Guid, CatalogProject>();
        private readonly HashSet<Guid> _warmupAttempted = new HashSet<Guid>();
        private readonly Dictionary<Guid, Guid> _partToModel = new Dictionary<Guid, Guid>();
        private readonly Dictionary<Guid, CachedSearchService> _modelSearchServices =
            new Dictionary<Guid, CachedSearchService>();
        private readonly Dictionary<string, CachedSearchService> _partSearchServices =
            new Dictionary<string, CachedSearchService>(StringComparer.Ordinal);
        private string _loadedProjectFilterKey = "";
        private int _warmupGeneration;
        private long _searchAccessSequence;
        private bool _resourcePressure;
        private DateTime _lastCacheMeasurementUtc;
        private long _lastSearchCacheBytes;
        private int _catalogProgressCompleted;
        private int _catalogProgressTotal;
        private bool _catalogProgressRunning;
        private string _catalogProgressDescription = "Каталог ещё не обновлялся";
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
            IBimBridgeLog log)
        {
            _objectsRepository = objectsRepository;
            _pilotSearchService = pilotSearchService;
            _modelManager = modelManager;
            _storageProvider = storageProvider;
            _searchManager = searchManager;
            _tabServiceProvider = tabServiceProvider;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log;
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            _catalogChangedSignal = new CoalescingSignal(
                TimeSpan.FromMilliseconds(250),
                () => CatalogChanged?.Invoke(this, EventArgs.Empty));

            if (_modelManager != null)
            {
                _modelManager.ModelOpened += OnModelChanged;
                _modelManager.ModelLoaded += OnModelChanged;
                _modelManager.ModelClosed += OnModelChanged;
            }

            _backgroundTask = Task.Run(() => BackgroundLoopAsync(_lifetime.Token));
        }

        public event EventHandler CatalogChanged;

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
            var usage = BimBridgeResourceMonitor.Capture();
            bool pressureChanged;
            var currentPressure = UpdateResourcePressure(usage, out pressureChanged);
            int modelServices;
            int partServices;
            lock (_gate)
            {
                modelServices = _modelSearchServices.Count;
                partServices = _partSearchServices.Count;
            }
            var cacheBytes = MeasureSearchCacheBytes();
            return string.Format(
                "Диск C: свободно {0} ГБ, резерв {1} ГБ. ОЗУ: свободно {2} ГБ, процесс {3} ГБ. Индексы поиска: {4} ГБ. Активные сервисы: {5}/{6}. Ограничитель: {7}.",
                FormatGigabytes(usage.IsDiskKnown ? (long?)usage.FreeDiskBytes : null),
                _settings.ResourceLimits.MinimumFreeDiskSpaceGb,
                FormatGigabytes(usage.IsMemoryKnown ? (long?)usage.AvailableMemoryBytes : null),
                FormatGigabytes(usage.ProcessPrivateBytes),
                FormatGigabytes(cacheBytes),
                modelServices,
                partServices,
                currentPressure ? "активен" : "не активен");
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
            lock (_gate)
            {
                _catalogProgressRunning = true;
                _catalogProgressCompleted = 0;
                _catalogProgressTotal = 0;
                _catalogProgressDescription = "Поиск BIM-моделей";
            }
            RaiseCatalogChanged();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken))
            {
                await _refreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    var modelIds = await FindCoordinationModelIdsAsync(linked.Token).ConfigureAwait(false);
                    var searchRoots = await Task.Run(
                        () => LoadSnapshots(modelIds, linked.Token),
                        linked.Token).ConfigureAwait(false);
                    // In Pilot 26.13 GetRootObject can explicitly be unsupported.
                    // Search roots are sufficient, so that optional source must not
                    // invalidate the complete catalog refresh.
                    var root = await TryGetRootSnapshotAsync().ConfigureAwait(false);
                    var roots = searchRoots
                        .Concat(root == null ? Enumerable.Empty<PilotObjectSnapshot>() : new[] { root })
                        .Where(x => x != null && x.Id != Guid.Empty)
                        .GroupBy(x => x.Id)
                        .Select(x => x.First())
                        .ToArray();
                    var scan = await Task.Run(
                        () => ScanTree(roots, linked.Token),
                        linked.Token).ConfigureAwait(false);
                    await Task.Run(
                        () => EnrichModelPaths(scan, roots, linked.Token),
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
                            roots.Length,
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
                    }
                    RaiseCatalogChanged();
                    throw;
                }
                catch
                {
                    lock (_gate)
                    {
                        _catalogProgressRunning = false;
                        _catalogProgressDescription = "Не удалось обновить каталог BIM-моделей";
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
            lock (_gate) catalog = _models.Values.Select(x => x.Clone()).ToArray();

            // Pilot SDK objects are apartment-bound.  Keep SDK access on the UI
            // dispatcher, but yield after a small batch so input, rendering and
            // modal Pilot notifications are never blocked by the whole catalog.
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
                        IModelStorage storage = null;
                        try { storage = _storageProvider?.GetStorage(entry.ModelId); }
                        catch { }

                        var version = viewer == null ? SafeGetLatestVersion(storage) : viewer.ModelVersion;
                        return new ModelDescriptor
                        {
                            ModelId = entry.ModelId,
                            ModelKey = entry.ModelId.ToString("D"),
                            DisplayName = entry.DisplayName,
                            ProjectId = entry.ProjectId,
                            ProjectName = entry.ProjectName,
                            ProjectPath = entry.ProjectPath,
                            VersionUtc = version == DateTime.MinValue ? null : version.ToUniversalTime().ToString("O"),
                            IsActive = activeViewer != null,
                            IsOpen = viewers.Length > 0,
                            IsLoaded = storage?.IsLoaded == true,
                            IsStorageReady = string.Equals(entry.StorageState, ServiceStates.Ready, StringComparison.Ordinal),
                            IsSearchReady = string.Equals(entry.SearchState, ServiceStates.Ready, StringComparison.Ordinal),
                            StorageState = entry.StorageState,
                            SearchState = entry.SearchState,
                            LastServiceError = entry.LastServiceError,
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

        public async Task<ModelAccess> GetModelAsync(string targetKey, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(targetKey, out var targetId))
                throw new BimBridgeException(
                    BimBridgeErrorCodes.InvalidRequest,
                    "TargetModelKey must be a coordination model or model part GUID.");

            var resolved = await ResolveTargetAsync(targetId, cancellationToken).ConfigureAwait(false);
            var storage = await GetReadyStorageAsync(resolved.ModelId, cancellationToken).ConfigureAwait(false);
            var version = await OnUiAsync(() =>
            {
                var activeTab = _tabServiceProvider?.GetActiveTabPageId() ?? Guid.Empty;
                var viewers = SafeGetViewers(resolved.ModelId);
                var viewer = viewers.FirstOrDefault(x => x.TabId == activeTab) ?? viewers.FirstOrDefault();
                return viewer == null ? SafeGetLatestVersion(storage) : viewer.ModelVersion;
            }).ConfigureAwait(false);

            if (version == DateTime.MinValue)
                throw ModelUnavailable(targetId, "No model version is available.");

            return new ModelAccess(resolved.ModelId, version, storage, resolved.TargetParts);
        }

        public async Task<IModelSearchService> GetSearchServiceAsync(
            ModelAccess access,
            IEnumerable<Guid> requestedPartIds,
            CancellationToken cancellationToken)
        {
            if (_searchManager == null)
                throw ModelUnavailable(access.ModelId, "BIM search service is unavailable.");

            var parts = (requestedPartIds ?? access.TargetModelPartIds ?? Array.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            await ResolveModelPartsAsync(
                access.ModelId,
                parts,
                true,
                cancellationToken).ConfigureAwait(false);
            ValidateParts(access.ModelId, parts);
            if (parts.Length == 0)
                return await GetWholeModelSearchServiceAsync(access.ModelId, cancellationToken).ConfigureAwait(false);

            var key = string.Join(";", parts.Select(x => x.ToString("D")));
            lock (_gate)
            {
                if (_partSearchServices.TryGetValue(key, out var cached))
                {
                    cached.LastAccess = ++_searchAccessSequence;
                    return cached.Service;
                }
            }

            await _searchInitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_partSearchServices.TryGetValue(key, out var cachedAfterWait))
                    {
                        cachedAfterWait.LastAccess = ++_searchAccessSequence;
                        return cachedAfterWait.Service;
                    }
                }
                EnsureSearchResourcesAvailable();
                var serviceTask = await OnUiAsync(
                    () => _searchManager.GetModelPartsSearchServiceAsync(parts)).ConfigureAwait(false);
                var service = await AwaitSdkTaskAsync(
                    serviceTask,
                    SearchServiceLoadTimeout,
                    cancellationToken,
                    "Model-part search service did not become ready in time.").ConfigureAwait(false);
                if (service == null) throw new InvalidOperationException("IModelSearchManager returned null.");
                IModelSearchService[] evicted;
                lock (_gate)
                {
                    _partSearchServices[key] = new CachedSearchService(access.ModelId, service, ++_searchAccessSequence);
                    evicted = TrimPartSearchCacheLocked();
                }
                DisposeSearchServices(evicted);
                return service;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (BimBridgeException exception) when (
                string.Equals(exception.Code, BimBridgeErrorCodes.ResourceLimit, StringComparison.Ordinal))
            {
                throw;
            }
            catch (Exception exception)
            {
                throw ModelUnavailable(access.ModelId, "Unable to initialize model-part search.", exception);
            }
            finally
            {
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

        private async Task BackgroundLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _log?.Error("BIM model catalog refresh failed.", exception);
                    try { await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private async Task<Guid[]> FindCoordinationModelIdsAsync(CancellationToken cancellationToken)
        {
            if (_pilotSearchService == null)
                throw new BimBridgeException(
                    BimBridgeErrorCodes.ModelNotAvailable,
                    "Pilot ISearchService is unavailable for coordination model discovery.",
                    true);

            var ids = new HashSet<Guid>();
            Exception searchError = null;
            var searchCompleted = false;
            using (var completed = new ManualResetEventSlim(false))
            {
                IDisposable subscription = null;
                var observer = new SearchResultObserver(
                    result =>
                    {
                        if (result?.Result != null)
                        {
                            lock (ids)
                            {
                                foreach (var id in result.Result.Where(x => x != Guid.Empty))
                                    ids.Add(id);
                            }
                        }
                    },
                    error =>
                    {
                        searchError = error;
                        completed.Set();
                    },
                    () =>
                    {
                        searchCompleted = true;
                        completed.Set();
                    });

                try
                {
                    await OnUiAsync(() =>
                    {
                        var query = _pilotSearchService
                            .GetObjectQueryBuilder()
                            .WithTypeFilter(TypeNames.CoordinationModel)
                            .MaxResults(100000);
                        subscription = _pilotSearchService.Search(query).Subscribe(observer);
                        return true;
                    }).ConfigureAwait(false);
                    var signaled = await Task.Run(
                        () => completed.Wait(SubscribeTimeout, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    if (!signaled)
                        _log?.Warn("Pilot coordination-model search timed out; the complete result was not received.");
                    if (searchError != null)
                        throw searchError;
                    if (signaled && !searchCompleted)
                        _log?.Warn("Pilot coordination-model search ended without OnCompleted.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    throw new BimBridgeException(
                        BimBridgeErrorCodes.ModelNotAvailable,
                        "Pilot search failed while discovering coordination models.",
                        true,
                        exception);
                }
                finally
                {
                    subscription?.Dispose();
                }
            }

            lock (ids) return ids.OrderBy(x => x).ToArray();
        }

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
                pending.Enqueue(new PendingObject(root.Id, root.Id));

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
                    if (!snapshots.TryGetValue(item.Id, out var snapshot)) continue;
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

        private async Task<PilotObjectSnapshot> TryGetRootSnapshotAsync()
        {
            try
            {
                return await OnUiAsync(() =>
                    CopySnapshot(_objectsRepository?.GetRootObject())).ConfigureAwait(false);
            }
            catch (NotSupportedException exception)
            {
                _log?.Warn("Pilot root-object traversal is unavailable; BIM catalog will use search roots. " + exception.Message);
                return null;
            }
            catch (Exception exception)
            {
                _log?.Warn("Pilot root-object traversal failed; BIM catalog will use search roots. " + exception.Message);
                return null;
            }
        }

        private void EnrichModelPaths(
            CatalogScan scan,
            IEnumerable<PilotObjectSnapshot> roots,
            CancellationToken cancellationToken)
        {
            var known = (roots ?? Enumerable.Empty<PilotObjectSnapshot>())
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
                var loaded = LoadSnapshots(frontier, cancellationToken)
                    .Where(x => x != null)
                    .ToArray();
                foreach (var item in loaded) known[item.Id] = item;
                frontier = loaded
                    .Select(x => x.ParentId)
                    .Where(x => x != Guid.Empty && !known.ContainsKey(x))
                    .Distinct()
                    .ToArray();
            }

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
                if (storage != null && storage.ModelId == targetId)
                {
                    AddResolvedModel(targetId, "BIM " + targetId.ToString("D"), null);
                    await MergeStoragePartsAsync(targetId, storage, cancellationToken).ConfigureAwait(false);
                    return new ResolvedTarget(targetId, Array.Empty<Guid>());
                }
            }
            catch { }

            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (TryResolveFromCatalog(targetId, out resolved)) return resolved;
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
                if (_models.ContainsKey(id))
                {
                    resolved = new ResolvedTarget(id, Array.Empty<Guid>());
                    return true;
                }
                if (_partToModel.TryGetValue(id, out var modelId))
                {
                    resolved = new ResolvedTarget(modelId, new[] { id });
                    return true;
                }
            }
            resolved = null;
            return false;
        }

        private async Task<IModelStorage> GetReadyStorageAsync(Guid modelId, CancellationToken cancellationToken)
        {
            IModelStorage storage;
            try
            {
                lock (_gate) SetServiceStateLocked(modelId, ServiceStates.Loading, null, null);
                RaiseCatalogChanged();
                storage = await OnUiAsync(() => _storageProvider?.GetStorage(modelId)).ConfigureAwait(false);
                if (storage == null) throw new InvalidOperationException("IModelStorageProvider returned null.");

                await WaitForStorageAsync(storage, cancellationToken).ConfigureAwait(false);

                await MergeStoragePartsAsync(modelId, storage, cancellationToken).ConfigureAwait(false);
                lock (_gate) SetServiceStateLocked(modelId, ServiceStates.Ready, null, null);
                RaiseCatalogChanged();
                return storage;
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException exception)
            {
                // GetStorage does not force a closed Pilot model to be loaded.  A
                // storage that stays cold is therefore an available on-demand
                // resource, not a broken service.  Keep the task retryable so it
                // can succeed after the model is opened or loaded by Pilot.
                lock (_gate) SetServiceStateLocked(modelId, ServiceStates.OnDemand, null, null);
                RaiseCatalogChanged();
                throw ModelUnavailable(
                    modelId,
                    "Model storage is not loaded yet. Open the model in Pilot-BIM and retry the request.",
                    exception);
            }
            catch (Exception exception)
            {
                lock (_gate) SetServiceStateLocked(modelId, ServiceStates.Error, null, exception.Message);
                RaiseCatalogChanged();
                throw ModelUnavailable(modelId, "Unable to initialize model storage.", exception);
            }
        }

        private void StartWarmup()
        {
            int generation;
            lock (_gate) generation = _warmupGeneration;
            _ = Task.Run(async () =>
            {
                if (!await _warmupGate.WaitAsync(0).ConfigureAwait(false)) return;
                try
                {
                    Guid[] modelIds;
                    lock (_gate)
                    {
                        modelIds = _models.Values
                            .Where(x => _warmupAttempted.Add(x.ModelId))
                            .OrderBy(x => x.ModelId)
                            .Select(x => x.ModelId)
                            .ToArray();
                        _catalogProgressRunning = modelIds.Length > 0;
                        _catalogProgressCompleted = 0;
                        _catalogProgressTotal = modelIds.Length;
                        _catalogProgressDescription = modelIds.Length > 0
                            ? "Проверка хранилищ BIM-моделей"
                            : (_models.Count > 0 ? "Каталог BIM-моделей готов" : "BIM-модели не найдены");
                    }
                    RaiseCatalogChanged();
                    if (modelIds.Length > 0)
                        _log?.Info(string.Format(
                            "BIM model storage probe queued: {0} models, {1} concurrent model slots. Search indexes are on-demand.",
                            modelIds.Length,
                            WarmupParallelism));
                    var completed = 0;
                    for (var start = 0; start < modelIds.Length; start += WarmupParallelism)
                    {
                        _lifetime.Token.ThrowIfCancellationRequested();
                        lock (_gate)
                        {
                            if (generation != _warmupGeneration) break;
                        }
                        var batch = modelIds.Skip(start).Take(WarmupParallelism)
                            .Select(modelId => WarmModelServicesAsync(modelId, _lifetime.Token)).ToArray();
                        await Task.WhenAll(batch).ConfigureAwait(false);
                        completed += batch.Length;
                        lock (_gate)
                        {
                            if (generation == _warmupGeneration)
                            {
                                _catalogProgressCompleted = completed;
                                _catalogProgressTotal = modelIds.Length;
                            }
                        }
                        RaiseCatalogChanged();
                        if (completed == modelIds.Length || completed % 100 == 0)
                            _log?.Info(string.Format(
                                "BIM model service warm-up progress: {0}/{1} models.",
                                completed,
                                modelIds.Length));
                    }
                }
                finally
                {
                    _warmupGate.Release();
                    bool restart;
                    lock (_gate)
                    {
                        restart = generation != _warmupGeneration;
                        if (!restart)
                        {
                            _catalogProgressRunning = false;
                            _catalogProgressCompleted = _catalogProgressTotal;
                            _catalogProgressDescription = _models.Count > 0
                                ? "Каталог BIM-моделей готов"
                                : "BIM-модели не найдены";
                        }
                    }
                    RaiseCatalogChanged();
                    if (restart) StartWarmup();
                }
            });
        }

        private async Task WarmModelServicesAsync(Guid modelId, CancellationToken cancellationToken)
        {
            try
            {
                await ProbeStorageAsync(modelId, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_models.TryGetValue(modelId, out var model) &&
                        !string.Equals(model.SearchState, ServiceStates.Ready, StringComparison.Ordinal))
                        SetServiceStateLocked(modelId, null, ServiceStates.OnDemand, null);
                }
                RaiseCatalogChanged();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lock (_gate) _warmupAttempted.Remove(modelId);
                _log?.Warn("BIM model storage probe incomplete for " + modelId.ToString("D") + ": " + FormatException(exception));
            }
        }

        private async Task ProbeStorageAsync(Guid modelId, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate) SetServiceStateLocked(modelId, ServiceStates.Loading, null, null);
                RaiseCatalogChanged();

                var storage = await OnUiAsync(() => _storageProvider?.GetStorage(modelId)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (storage == null)
                {
                    lock (_gate) SetServiceStateLocked(modelId, ServiceStates.OnDemand, null, null);
                    RaiseCatalogChanged();
                    return;
                }

                if (!storage.IsLoaded)
                {
                    // There is no public force-load method on IModelStorageProvider.
                    // Do not occupy a warm-up slot for 30 seconds and do not report
                    // a normal closed-model state as an SDK failure.
                    lock (_gate) SetServiceStateLocked(modelId, ServiceStates.OnDemand, null, null);
                    RaiseCatalogChanged();
                    return;
                }

                await MergeStoragePartsAsync(modelId, storage, cancellationToken).ConfigureAwait(false);
                lock (_gate) SetServiceStateLocked(modelId, ServiceStates.Ready, null, null);
                RaiseCatalogChanged();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                lock (_gate) SetServiceStateLocked(modelId, ServiceStates.Error, null, exception.Message);
                RaiseCatalogChanged();
                throw ModelUnavailable(modelId, "Unable to inspect model storage.", exception);
            }
        }

        private async Task<IModelSearchService> GetWholeModelSearchServiceAsync(
            Guid modelId,
            CancellationToken cancellationToken)
        {
            if (_searchManager == null)
                throw ModelUnavailable(modelId, "BIM search service is unavailable.");

            lock (_gate)
            {
                if (_modelSearchServices.TryGetValue(modelId, out var cached))
                {
                    cached.LastAccess = ++_searchAccessSequence;
                    return cached.Service;
                }
                SetServiceStateLocked(modelId, null, ServiceStates.Loading, null);
            }
            RaiseCatalogChanged();

            await _searchInitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_modelSearchServices.TryGetValue(modelId, out var cachedAfterWait))
                    {
                        cachedAfterWait.LastAccess = ++_searchAccessSequence;
                        return cachedAfterWait.Service;
                    }
                }
                EnsureSearchResourcesAvailable();
                var serviceTask = await OnUiAsync(
                    () => _searchManager.GetModelSearchServiceAsync(modelId)).ConfigureAwait(false);
                var service = await AwaitSdkTaskAsync(
                    serviceTask,
                    SearchServiceLoadTimeout,
                    cancellationToken,
                    "Model search service did not become ready in time.").ConfigureAwait(false);
                if (service == null) throw new InvalidOperationException("IModelSearchManager returned null.");
                IModelSearchService[] evicted;
                lock (_gate)
                {
                    _modelSearchServices[modelId] = new CachedSearchService(modelId, service, ++_searchAccessSequence);
                    SetServiceStateLocked(modelId, null, ServiceStates.Ready, null);
                    evicted = TrimModelSearchCacheLocked(modelId);
                }
                DisposeSearchServices(evicted);
                RaiseCatalogChanged();
                return service;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (BimBridgeException exception) when (
                string.Equals(exception.Code, BimBridgeErrorCodes.ResourceLimit, StringComparison.Ordinal))
            {
                lock (_gate) SetServiceStateLocked(modelId, null, ServiceStates.OnDemand, exception.Message);
                RaiseCatalogChanged();
                throw;
            }
            catch (Exception exception)
            {
                SetSearchError(modelId, exception);
                throw ModelUnavailable(modelId, "Unable to initialize model search.", exception);
            }
            finally
            {
                _searchInitializationGate.Release();
            }
        }

        private async Task WaitForStorageAsync(IModelStorage storage, CancellationToken cancellationToken)
        {
            if (storage.IsLoaded) return;

            var loaded = new TaskCompletionSource<bool>();
            EventHandler onLoaded = (sender, args) => loaded.TrySetResult(true);
            storage.Loaded += onLoaded;
            try
            {
                if (storage.IsLoaded) return;
                var timeout = Task.Delay(StorageLoadTimeout, cancellationToken);
                var completed = await Task.WhenAny(loaded.Task, timeout).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (completed != loaded.Task || !storage.IsLoaded)
                    throw new TimeoutException("Model storage did not become ready in time.");
            }
            finally
            {
                storage.Loaded -= onLoaded;
            }
        }

        private static async Task<T> AwaitSdkTaskAsync<T>(
            Task<T> task,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            string timeoutMessage)
        {
            if (task == null) throw new InvalidOperationException("Pilot SDK returned a null task.");
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != task) throw new TimeoutException(timeoutMessage);
            return await task.ConfigureAwait(false);
        }

        private void EnsureSearchResourcesAvailable()
        {
            var usage = BimBridgeResourceMonitor.Capture();
            bool changed;
            var pressure = UpdateResourcePressure(usage, out changed);

            if (changed)
            {
                if (pressure)
                    _log?.Warn("BIM search cache limiter activated. " + BuildResourceMessage(usage));
                else
                    _log?.Info("BIM search cache limiter released. " + BuildResourceMessage(usage));
                RaiseCatalogChanged();
            }

            if (pressure)
                throw new BimBridgeException(
                    BimBridgeErrorCodes.ResourceLimit,
                    "A new BIM search index was not created because the local resource reserve is active. " +
                    BuildResourceMessage(usage),
                    true);
        }

        private bool UpdateResourcePressure(BimBridgeResourceSnapshot usage, out bool changed)
        {
            var limits = _settings.ResourceLimits;
            var freeDiskGb = usage.FreeDiskBytes / (double)(1024L * 1024 * 1024);
            var availableMemoryGb = usage.AvailableMemoryBytes / (double)(1024L * 1024 * 1024);
            lock (_gate)
            {
                var before = _resourcePressure;
                var diskPressure = !usage.IsDiskKnown ||
                    freeDiskGb < (_resourcePressure
                        ? limits.CacheResumeThresholdGb
                        : limits.CacheCreationThresholdGb);
                var memoryPressure = usage.IsMemoryKnown &&
                    availableMemoryGb < limits.MinimumAvailableMemoryGb;
                _resourcePressure = diskPressure || memoryPressure;
                changed = before != _resourcePressure;
                return _resourcePressure;
            }
        }

        private string BuildResourceMessage(BimBridgeResourceSnapshot usage)
        {
            return string.Format(
                "Free disk: {0} GB; index creation threshold: {1} GB; protected minimum: {2} GB; available RAM: {3} GB; required RAM: {4} GB.",
                FormatGigabytes(usage.IsDiskKnown ? (long?)usage.FreeDiskBytes : null),
                _settings.ResourceLimits.CacheCreationThresholdGb,
                _settings.ResourceLimits.MinimumFreeDiskSpaceGb,
                FormatGigabytes(usage.IsMemoryKnown ? (long?)usage.AvailableMemoryBytes : null),
                _settings.ResourceLimits.MinimumAvailableMemoryGb);
        }

        private IModelSearchService[] TrimModelSearchCacheLocked(Guid protectedModelId)
        {
            var result = new List<IModelSearchService>();
            while (_modelSearchServices.Count > _settings.ResourceLimits.MaxCachedModelSearchServices)
            {
                var victim = _modelSearchServices
                    .Where(x => x.Key != protectedModelId)
                    .OrderBy(x => x.Value.LastAccess)
                    .FirstOrDefault();
                if (victim.Value == null) break;
                _modelSearchServices.Remove(victim.Key);
                if (_models.TryGetValue(victim.Key, out var model) &&
                    string.Equals(model.SearchState, ServiceStates.Ready, StringComparison.Ordinal))
                    SetServiceStateLocked(victim.Key, null, ServiceStates.OnDemand, null);
                result.Add(victim.Value.Service);
            }
            return result.ToArray();
        }

        private IModelSearchService[] TrimPartSearchCacheLocked()
        {
            var result = new List<IModelSearchService>();
            while (_partSearchServices.Count > _settings.ResourceLimits.MaxCachedPartSearchServices)
            {
                var victim = _partSearchServices.OrderBy(x => x.Value.LastAccess).First();
                _partSearchServices.Remove(victim.Key);
                result.Add(victim.Value.Service);
            }
            return result.ToArray();
        }

        private void ApplyResourceLimits()
        {
            IModelSearchService[] evicted;
            lock (_gate)
            {
                evicted = TrimModelSearchCacheLocked(Guid.Empty)
                    .Concat(TrimPartSearchCacheLocked())
                    .Distinct()
                    .ToArray();
            }
            DisposeSearchServices(evicted);
            if (evicted.Length > 0) RaiseCatalogChanged();
        }

        private static void DisposeSearchServices(IEnumerable<IModelSearchService> services)
        {
            foreach (var service in (services ?? Enumerable.Empty<IModelSearchService>()).Where(x => x != null).Distinct())
            {
                try { service.Dispose(); }
                catch { }
            }
        }

        private long MeasureSearchCacheBytes()
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _lastCacheMeasurementUtc < TimeSpan.FromSeconds(30))
                    return _lastSearchCacheBytes;
            }

            long total = 0;
            try
            {
                var directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ASCON", "Pilot-BIM", "Pilot-BIM-Viewer", "Temp", "SearchIndexes");
                if (Directory.Exists(directory))
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        try { total += new FileInfo(file).Length; }
                        catch { }
                    }
                }
            }
            catch { }

            lock (_gate)
            {
                _lastSearchCacheBytes = total;
                _lastCacheMeasurementUtc = DateTime.UtcNow;
                return _lastSearchCacheBytes;
            }
        }

        private static string FormatGigabytes(long? bytes)
        {
            if (!bytes.HasValue || bytes.Value < 0) return "unknown";
            return (bytes.Value / (double)(1024L * 1024 * 1024)).ToString("0.0");
        }

        private static string FormatException(Exception exception)
        {
            var messages = new List<string>();
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
                    messages.Add(current.Message);
            }
            return string.Join(" -> ", messages);
        }

        private void MergeScan(CatalogScan scan)
        {
            IModelSearchService[] staleSearchServices = Array.Empty<IModelSearchService>();
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
                    staleSearchServices = _modelSearchServices.Values
                        .Concat(_partSearchServices.Values)
                        .Select(x => x.Service)
                        .Distinct()
                        .ToArray();
                    _loadedProjectFilterKey = filterKey;
                    _warmupGeneration++;
                    _warmupAttempted.Clear();
                    _modelSearchServices.Clear();
                    _partSearchServices.Clear();
                }
                else
                {
                    _warmupAttempted.IntersectWith(selectedModelIds);
                    foreach (var model in oldModels.Values)
                    {
                        if (selectedModelIds.Contains(model.ModelId) &&
                            (string.Equals(model.StorageState, ServiceStates.OnDemand, StringComparison.Ordinal) ||
                             string.Equals(model.StorageState, ServiceStates.Error, StringComparison.Ordinal)))
                            _warmupAttempted.Remove(model.ModelId);
                    }
                }
                _models.Clear();
                _partToModel.Clear();
                _projects.Clear();
                foreach (var project in scan.Projects.Values.Where(x => selectedProjectIds.Contains(x.Id)))
                    _projects[project.Id] = new CatalogProject(project.Id, project.ParentId, project.DisplayName);
                foreach (var scanned in selectedModels)
                {
                    if (oldModels.TryGetValue(scanned.ModelId, out var previous))
                    {
                        scanned.StorageState = previous.StorageState;
                        scanned.SearchState = filterChanged ? ServiceStates.OnDemand : previous.SearchState;
                        scanned.LastServiceError = previous.LastServiceError;
                    }
                    _models[scanned.ModelId] = scanned;
                    foreach (var part in scanned.Parts.Values)
                        _partToModel[part.ModelPartId] = scanned.ModelId;
                }
            }
            DisposeSearchServices(staleSearchServices);
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
            lock (_gate)
            {
                if (!_models.TryGetValue(modelId, out var model))
                {
                    model = new CatalogModel(modelId, displayName);
                    _models[modelId] = model;
                }
                if (part != null)
                {
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
            try { parts = storage.GetModelPartsIds()?.Where(x => x != Guid.Empty).Distinct().ToArray() ?? Array.Empty<Guid>(); }
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
                    if (!_models.ContainsKey(args.Viewer.ModelId))
                        _models[args.Viewer.ModelId] =
                            new CatalogModel(args.Viewer.ModelId, "BIM " + args.Viewer.ModelId.ToString("D"));
                    _warmupAttempted.Remove(args.Viewer.ModelId);
                }
                RaiseCatalogChanged();
            }
            _ = Task.Run(async () =>
            {
                try { await RefreshAsync(_lifetime.Token).ConfigureAwait(false); }
                catch { }
            });
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
            string.Equals(typeName, TypeNames.CoordinationModel, StringComparison.Ordinal);

        private static bool IsModelPart(string typeName) =>
            string.Equals(typeName, TypeNames.ModelPart, StringComparison.Ordinal) ||
            string.Equals(typeName, TypeNames.ModelPartCloudPoint, StringComparison.Ordinal) ||
            string.Equals(typeName, "bim_modelPart_other3Dmodel", StringComparison.OrdinalIgnoreCase);

        private static BimBridgeException ModelUnavailable(Guid id, string details, Exception inner = null) =>
            new BimBridgeException(
                BimBridgeErrorCodes.ModelNotAvailable,
                string.Format("Model or model part {0:D} is not available. {1}", id, details),
                true,
                inner);

        private Task<T> OnUiAsync<T>(Func<T> action)
        {
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) return Task.FromResult(action());
            return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ModelContextProvider));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_modelManager != null)
            {
                _modelManager.ModelOpened -= OnModelChanged;
                _modelManager.ModelLoaded -= OnModelChanged;
                _modelManager.ModelClosed -= OnModelChanged;
            }
            _lifetime.Cancel();
            _catalogChangedSignal.Dispose();
            IModelSearchService[] services;
            lock (_gate)
            {
                services = _modelSearchServices.Values
                    .Concat(_partSearchServices.Values)
                    .Select(x => x.Service)
                    .Distinct()
                    .ToArray();
                _modelSearchServices.Clear();
                _partSearchServices.Clear();
            }
            DisposeSearchServices(services);
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

        private sealed class CachedSearchService
        {
            public CachedSearchService(Guid modelId, IModelSearchService service, long lastAccess)
            {
                ModelId = modelId;
                Service = service;
                LastAccess = lastAccess;
            }

            public Guid ModelId { get; }
            public IModelSearchService Service { get; }
            public long LastAccess { get; set; }
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
            public Dictionary<Guid, CatalogModel> Models { get; } = new Dictionary<Guid, CatalogModel>();
            public Dictionary<Guid, PilotObjectSnapshot> Projects { get; } = new Dictionary<Guid, PilotObjectSnapshot>();
            public Dictionary<Guid, Guid> PartToModel { get; } = new Dictionary<Guid, Guid>();
            public HashSet<string> ObservedTypes { get; } =
                new HashSet<string>(StringComparer.Ordinal);
            public int ScannedObjects { get; private set; }

            public void RegisterObject(PilotObjectSnapshot snapshot)
            {
                ScannedObjects++;
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
                var model = GetOrAddModel(modelId, modelId.ToString("D"));
                model.Parts[snapshot.Id] =
                    new CatalogPart(snapshot.Id, snapshot.DisplayName, snapshot.TypeName);
                PartToModel[snapshot.Id] = modelId;
            }
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
