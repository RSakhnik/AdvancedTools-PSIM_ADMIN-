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
    public sealed partial class ModelContextProvider
    {
        private async Task<IModelStorage> GetReadyStorageAsync(Guid modelId, CancellationToken cancellationToken)
        {
            IModelStorage storage = null;
            try
            {
                lock (_gate) SetServiceStateLocked(modelId, ServiceStates.Loading, null, null);
                RaiseCatalogChanged();
                storage = await OnUiAsync(() => _storageProvider?.GetStorage(modelId), cancellationToken).ConfigureAwait(false);
                if (storage == null) throw new InvalidOperationException("IModelStorageProvider returned null.");
                RegisterStorage(storage);

                await WaitForStorageAsync(storage, cancellationToken).ConfigureAwait(false);

                await MergeStoragePartsAsync(modelId, storage, cancellationToken).ConfigureAwait(false);
                lock (_gate) SetServiceStateLocked(modelId, ServiceStates.Ready, null, null);
                RaiseCatalogChanged();
                return storage;
            }
            catch (OperationCanceledException)
            {
                ReleaseStorage(storage);
                throw;
            }
            catch (TimeoutException exception)
            {
                ReleaseStorage(storage);
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
            catch (BimBridgeException)
            {
                ReleaseStorage(storage);
                throw;
            }
            catch (Exception exception)
            {
                ReleaseStorage(storage);
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
                _log?.Warn("BIM model storage probe incomplete for " + modelId.ToString("D") + ": " + FormatException(exception));
            }
        }

        private async Task ProbeStorageAsync(Guid modelId, CancellationToken cancellationToken)
        {
            IModelStorage storage = null;
            try
            {
                Interlocked.Increment(ref _storageProbeCount);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate) SetServiceStateLocked(modelId, ServiceStates.Loading, null, null);
                RaiseCatalogChanged();

                storage = await OnUiAsync(() => _storageProvider?.GetStorage(modelId)).ConfigureAwait(false);
                RegisterStorage(storage);
                cancellationToken.ThrowIfCancellationRequested();
                if (storage == null)
                {
                    lock (_gate) SetServiceStateLocked(modelId, ServiceStates.OnDemand, null, null);
                    RaiseCatalogChanged();
                    return;
                }
                // The composition metadata is useful even when the model viewer
                // has not loaded geometry.  Treat storage as an enrichment source,
                // never as a prerequisite for the Pilot tree catalog.
                await MergeStoragePartsAsync(modelId, storage, cancellationToken).ConfigureAwait(false);
                if (!storage.IsLoaded)
                {
                    // There is no public force-load method on IModelStorageProvider.
                    // Do not occupy a warm-up slot for 30 seconds and do not report
                    // a normal closed-model state as an SDK failure.
                    lock (_gate) SetServiceStateLocked(modelId, ServiceStates.OnDemand, null, null);
                    RaiseCatalogChanged();
                    return;
                }

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
            finally
            {
                ReleaseStorage(storage);
            }
        }

        private async Task WaitForStorageAsync(IModelStorage storage, CancellationToken cancellationToken)
        {
            if (await OnUiAsync(() => storage.IsLoaded).ConfigureAwait(false)) return;

            var loaded = new TaskCompletionSource<bool>();
            EventHandler onLoaded = (sender, args) => loaded.TrySetResult(true);
            await OnUiAsync(() => { storage.Loaded += onLoaded; return true; }).ConfigureAwait(false);
            try
            {
                if (await OnUiAsync(() => storage.IsLoaded).ConfigureAwait(false)) return;
                var timeout = Task.Delay(StorageLoadTimeout, cancellationToken);
                var completed = await Task.WhenAny(loaded.Task, timeout).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (completed != loaded.Task || !await OnUiAsync(() => storage.IsLoaded).ConfigureAwait(false))
                    throw new TimeoutException("Model storage did not become ready in time.");
            }
            finally
            {
                await OnUiAsync(() => { storage.Loaded -= onLoaded; return true; }).ConfigureAwait(false);
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

        private async Task<IModelSearchService> AwaitOwnedSearchServiceAsync(
            Task<IModelSearchService> task,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            string timeoutMessage)
        {
            if (task == null) throw new InvalidOperationException("Pilot SDK returned a null task.");
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
            if (completed != task)
            {
                ObserveAndReleaseLateSearchService(task);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(timeoutMessage);
            }
            return await task.ConfigureAwait(false);
        }

        private readonly HashSet<Task> _pendingSdkCleanup = new HashSet<Task>();

        public Task GetPendingSdkCleanup()
        {
            lock (_gate) return Task.WhenAll(_pendingSdkCleanup.Concat(new[] { _sdkGateway.RecoveryTask }));
        }

        public bool IsPilotUiBlocked => _pilotUi.Blocked;
        public string SdkRecoveryReason => _sdkGateway.RecoveryReason;
        public string SdkStatus => _pilotUi.Reason + "; " + _pilotUi.WindowStatus + "; незавершённых вызовов: " + _sdkGateway.PendingCount + "; " + _sdkGateway.Status +
            (_pilotUi.Prolonged || _sdkGateway.HasProlongedRecovery ? ". Блокировка длится более 30 секунд. Если SDK продолжает зависать после закрытия уведомления, перезапустите Pilot вручную." : "");
        public bool ShowBlockingWindow() => _pilotUi.ShowPopup();

        private void ObserveAndReleaseLateSearchService(Task<IModelSearchService> task)
        {
            var cleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _pendingSdkCleanup.Add(cleanup.Task);
            task.ContinueWith(
                completed =>
                {
                    try
                    {
                    if (completed.Status == TaskStatus.RanToCompletion)
                        DisposeSearchServiceUntracked(completed.Result);
                    else if (completed.IsFaulted)
                    {
                        var ignored = completed.Exception;
                    }
                    }
                    finally
                    {
                        lock (_gate) _pendingSdkCleanup.Remove(cleanup.Task);
                        cleanup.TrySetResult(true);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void RegisterStorage(IModelStorage storage, bool notify = true)
        {
            if (storage == null) return;
            lock (_gate) _activeStorages++;
            if (notify) RaiseCatalogChanged();
        }

        private void ReleaseStorage(IModelStorage storage, bool notify = true)
        {
            if (storage == null) return;
            _sdkGateway.QueueCleanup(() => storage.Dispose(), () =>
            {
                lock (_gate) _activeStorages = Math.Max(0, _activeStorages - 1);
                if (notify) RaiseCatalogChanged();
            }, "Storage Dispose");
        }

        private void DisposeStorageUntracked(IModelStorage storage)
        {
            if (storage == null) return;
            _sdkGateway.QueueCleanup(() => storage.Dispose(), null, "Untracked storage Dispose");
        }

        private void ReleaseSearchService(Guid modelId, IModelSearchService service)
        {
            _sdkGateway.QueueCleanup(() => service.Dispose(), () =>
            {
                lock (_gate)
                {
                    _activeSearchServices = Math.Max(0, _activeSearchServices - 1);
                    SetServiceStateLocked(modelId, null, ServiceStates.OnDemand, null);
                }
                RaiseCatalogChanged();
            }, "Search Dispose");
        }

        private void DisposeSearchServiceUntracked(IModelSearchService service)
        {
            if (service == null) return;
            _sdkGateway.QueueCleanup(() => service.Dispose(), null, "Untracked search Dispose");
        }

        private void EnsureSearchResourcesAvailable(SearchResourceRequestKind requestKind)
        {
            var usage = _resourceMonitor.Capture();
            var decision = _resourcePolicy.Evaluate(usage, _settings.ResourceLimits, requestKind);
            string previousReason;
            bool previousExistingOnly;
            lock (_gate)
            {
                previousReason = _resourceRestrictionReason;
                previousExistingOnly = _existingIndexesOnly;
                _resourceRestrictionReason = decision.IsAllowed ? string.Empty : decision.Reason;
                _existingIndexesOnly = decision.ExistingIndexesOnly;
            }
            if (!string.Equals(previousReason, _resourceRestrictionReason, StringComparison.Ordinal) ||
                previousExistingOnly != _existingIndexesOnly)
            {
                if (!decision.IsAllowed)
                    _log?.Warn("BIM search resource limiter blocked a request. " + decision.Reason + " " + BuildResourceMessage(usage));
                else if (decision.ExistingIndexesOnly)
                    _log?.Info("BIM search resource limiter allows existing indexes only. " + BuildResourceMessage(usage));
                else
                    _log?.Info("BIM search resource limiter released. " + BuildResourceMessage(usage));
                RaiseCatalogChanged();
            }

            if (!decision.IsAllowed)
                throw new BimBridgeException(
                    BimBridgeErrorCodes.ResourceLimit,
                    decision.Reason + " " +
                    BuildResourceMessage(usage),
                    true);
        }

        private string BuildResourceMessage(BimBridgeResourceSnapshot usage)
        {
            return string.Format(
                "Free disk: {0} GB; index creation threshold: {1} GB; protected minimum: {2} GB; available RAM: {3} GB; new-index RAM: {4} GB; critical RAM: {5} GB.",
                FormatGigabytes(usage.IsDiskKnown ? (long?)usage.FreeDiskBytes : null),
                _settings.ResourceLimits.CacheCreationThresholdGb,
                _settings.ResourceLimits.MinimumFreeDiskSpaceGb,
                FormatGigabytes(usage.IsMemoryKnown ? (long?)usage.AvailableMemoryBytes : null),
                _settings.ResourceLimits.MinimumAvailableMemoryGb,
                _settings.ResourceLimits.CriticalAvailableMemoryGb);
        }

        public void ApplyResourceLimits()
        {
            _settings.ResourceLimits.Normalize();
            _searchIndexInventory.Capture(true);
            RaiseCatalogChanged();
        }

        private static string FormatGigabytes(long? bytes)
        {
            if (!bytes.HasValue || bytes.Value < 0) return "unknown";
            return (bytes.Value / (double)(1024L * 1024 * 1024)).ToString("0.0");
        }

        private static TaskCompletionSource<bool> NewReadinessSource() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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

    }
}
