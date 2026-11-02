using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AdvancedTools.BimBridge
{
    public sealed class ResourceLimitsViewModel : INotifyPropertyChanged, IDataErrorInfo
    {
        private static readonly string[] ValidatedProperties =
        {
            nameof(MinimumFreeDiskSpaceGb),
            nameof(CacheCreationThresholdGb),
            nameof(CacheResumeThresholdGb),
            nameof(MinimumAvailableMemoryGb),
            nameof(CriticalAvailableMemoryGb),
            nameof(MaxSearchResults),
            nameof(MaxPropertyElements), nameof(MaxSpatialTraversalNodes), nameof(MaxSpatialResultMiB), nameof(SdkDispatcherQueueTimeoutSeconds),
            nameof(SearchServiceInitializationTimeoutSeconds), nameof(SdkCleanupTimeoutSeconds)
        };

        private readonly Action<ResourceLimitSettings> _apply;
        private readonly Func<string> _getResourceStatus;
        private string _minimumFreeDiskSpaceGb;
        private string _cacheCreationThresholdGb;
        private string _cacheResumeThresholdGb;
        private string _minimumAvailableMemoryGb;
        private string _criticalAvailableMemoryGb;
        private bool _allowExistingSearchIndexesBelowMemoryLimit;
        private string _maxCachedModelSearchServices;
        private string _maxCachedPartSearchServices;
        private string _maxSearchResults;
        private string _maxPropertyElements;
        private string _maxSpatialTraversalNodes, _maxSpatialResultMiB;
        private string _resourceStatus;
        private readonly BimBridgeSettings _workerSettings;
        private string _queueTimeout, _initializationTimeout, _cleanupTimeout;
        public string SdkDispatcherQueueTimeoutSeconds { get => _queueTimeout; set => Set(ref _queueTimeout, value); }
        public string SearchServiceInitializationTimeoutSeconds { get => _initializationTimeout; set => Set(ref _initializationTimeout, value); }
        public string SdkCleanupTimeoutSeconds { get => _cleanupTimeout; set => Set(ref _cleanupTimeout, value); }

        public ResourceLimitsViewModel(
            ResourceLimitSettings source,
            Func<string> getResourceStatus,
            Action<ResourceLimitSettings> apply, BimBridgeSettings workerSettings = null)
        {
            _workerSettings = workerSettings ?? new BimBridgeSettings();
            LoadTimeouts(_workerSettings);
            _getResourceStatus = getResourceStatus ?? (() => "Данные о ресурсах недоступны");
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            Load(source ?? new ResourceLimitSettings());
            RefreshStatus();
            SaveCommand = new RelayCommand(Save, () => !HasErrors);
            ResetCommand = new RelayCommand(Reset);
            RefreshCommand = new RelayCommand(RefreshStatus);
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<bool> RequestClose;

        public ICommand SaveCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand CancelCommand { get; }

        public string MinimumFreeDiskSpaceGb { get => _minimumFreeDiskSpaceGb; set => Set(ref _minimumFreeDiskSpaceGb, value); }
        public string CacheCreationThresholdGb { get => _cacheCreationThresholdGb; set => Set(ref _cacheCreationThresholdGb, value); }
        public string CacheResumeThresholdGb { get => _cacheResumeThresholdGb; set => Set(ref _cacheResumeThresholdGb, value); }
        public string MinimumAvailableMemoryGb { get => _minimumAvailableMemoryGb; set => Set(ref _minimumAvailableMemoryGb, value); }
        public string CriticalAvailableMemoryGb { get => _criticalAvailableMemoryGb; set => Set(ref _criticalAvailableMemoryGb, value); }
        public bool AllowExistingSearchIndexesBelowMemoryLimit
        {
            get => _allowExistingSearchIndexesBelowMemoryLimit;
            set
            {
                if (_allowExistingSearchIndexesBelowMemoryLimit == value) return;
                _allowExistingSearchIndexesBelowMemoryLimit = value;
                OnPropertyChanged();
            }
        }
        public string MaxCachedModelSearchServices { get => _maxCachedModelSearchServices; set => Set(ref _maxCachedModelSearchServices, value); }
        public string MaxCachedPartSearchServices { get => _maxCachedPartSearchServices; set => Set(ref _maxCachedPartSearchServices, value); }
        public string MaxSearchResults { get => _maxSearchResults; set => Set(ref _maxSearchResults, value); }
        public string MaxPropertyElements { get => _maxPropertyElements; set => Set(ref _maxPropertyElements, value); }
        public string MaxSpatialTraversalNodes { get => _maxSpatialTraversalNodes; set => Set(ref _maxSpatialTraversalNodes, value); }
        public string MaxSpatialResultMiB { get => _maxSpatialResultMiB; set => Set(ref _maxSpatialResultMiB, value); }

        public string ResourceStatus
        {
            get => _resourceStatus;
            private set
            {
                if (string.Equals(_resourceStatus, value, StringComparison.Ordinal)) return;
                _resourceStatus = value;
                OnPropertyChanged();
            }
        }

        public string Error => string.Join(Environment.NewLine,
            ValidatedProperties.Select(x => this[x]).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());

        public string this[string columnName]
        {
            get
            {
                switch (columnName)
                {
                    case nameof(SdkDispatcherQueueTimeoutSeconds):
                        return ValidateRange(SdkDispatcherQueueTimeoutSeconds, 1, Math.Max(1, _workerSettings.OperationTimeoutSeconds), "Ожидание Dispatcher");
                    case nameof(SearchServiceInitializationTimeoutSeconds):
                        return ValidateRange(SearchServiceInitializationTimeoutSeconds, 1, Math.Max(1, _workerSettings.OperationTimeoutSeconds), "Открытие поиска");
                    case nameof(SdkCleanupTimeoutSeconds):
                        return ValidateRange(SdkCleanupTimeoutSeconds, 1, Math.Max(1, _workerSettings.OperationTimeoutSeconds), "Освобождение SDK");
                    case nameof(MinimumFreeDiskSpaceGb):
                        return ValidateRange(MinimumFreeDiskSpaceGb, 10, int.MaxValue, "Резерв диска");
                    case nameof(CacheCreationThresholdGb):
                        if (!TryRead(CacheCreationThresholdGb, out var creation) || !TryRead(MinimumFreeDiskSpaceGb, out var disk))
                            return "Введите целое число.";
                        return creation < disk + 5
                            ? "Порог создания должен быть минимум на 5 ГБ выше защищённого резерва."
                            : null;
                    case nameof(CacheResumeThresholdGb):
                        if (!TryRead(CacheResumeThresholdGb, out var resume) || !TryRead(CacheCreationThresholdGb, out creation))
                            return "Введите целое число.";
                        return resume <= creation
                            ? "Порог возобновления должен быть выше порога создания."
                            : null;
                    case nameof(CriticalAvailableMemoryGb):
                        return ValidateRange(CriticalAvailableMemoryGb, 1, int.MaxValue, "Аварийный резерв RAM");
                    case nameof(MinimumAvailableMemoryGb):
                        if (!TryRead(MinimumAvailableMemoryGb, out var minimumMemory) ||
                            !TryRead(CriticalAvailableMemoryGb, out var criticalMemory))
                            return "Введите целое число.";
                        return minimumMemory <= criticalMemory
                            ? "Порог нового индекса должен быть выше аварийного резерва RAM."
                            : null;
                    case nameof(MaxSearchResults):
                        return ValidateRange(MaxSearchResults, 100, 100000, "Результатов поиска");
                    case nameof(MaxPropertyElements):
                        return ValidateRange(MaxPropertyElements, 100, 100000, "Элементов свойств");
                    case nameof(MaxSpatialTraversalNodes):
                        return ValidateRange(MaxSpatialTraversalNodes, 1, 1000000, "Узлов пространственного обхода");
                    case nameof(MaxSpatialResultMiB):
                        return ValidateRange(MaxSpatialResultMiB, 1, 256, "Размер пространственного JSON, МиБ");
                    default:
                        return null;
                }
            }
        }

        public bool HasErrors => ValidatedProperties.Any(x => !string.IsNullOrWhiteSpace(this[x]));

        private void Save()
        {
            if (HasErrors) return;
            var value = CreateSettings();
            _workerSettings.SdkDispatcherQueueTimeoutSeconds = Read(SdkDispatcherQueueTimeoutSeconds);
            _workerSettings.SearchServiceInitializationTimeoutSeconds = Read(SearchServiceInitializationTimeoutSeconds);
            _workerSettings.SdkCleanupTimeoutSeconds = Read(SdkCleanupTimeoutSeconds);
            _apply(value);
            RefreshStatus();
            RequestClose?.Invoke(true);
        }

        private void Reset()
        {
            Load(new ResourceLimitSettings());
            LoadTimeouts(new BimBridgeSettings());
            OnPropertyChanged(string.Empty);
            CommandManager.InvalidateRequerySuggested();
        }

        private void RefreshStatus()
        {
            try { ResourceStatus = _getResourceStatus(); }
            catch { ResourceStatus = "Данные о ресурсах недоступны"; }
        }

        private ResourceLimitSettings CreateSettings()
        {
            return new ResourceLimitSettings
            {
                MinimumFreeDiskSpaceGb = Read(MinimumFreeDiskSpaceGb),
                CacheCreationThresholdGb = Read(CacheCreationThresholdGb),
                CacheResumeThresholdGb = Read(CacheResumeThresholdGb),
                MinimumAvailableMemoryGb = Read(MinimumAvailableMemoryGb),
                CriticalAvailableMemoryGb = Read(CriticalAvailableMemoryGb),
                AllowExistingSearchIndexesBelowMemoryLimit = AllowExistingSearchIndexesBelowMemoryLimit,
                MaxCachedModelSearchServices = Read(MaxCachedModelSearchServices),
                MaxCachedPartSearchServices = Read(MaxCachedPartSearchServices),
                MaxSearchResults = Read(MaxSearchResults),
                MaxPropertyElements = Read(MaxPropertyElements),
                MaxSpatialTraversalNodes = Read(MaxSpatialTraversalNodes),
                MaxSpatialResultBytes = (long)Read(MaxSpatialResultMiB)*1048576
            };
        }

        private void Load(ResourceLimitSettings value)
        {
            var copy = value.Clone();
            _minimumFreeDiskSpaceGb = copy.MinimumFreeDiskSpaceGb.ToString();
            _cacheCreationThresholdGb = copy.CacheCreationThresholdGb.ToString();
            _cacheResumeThresholdGb = copy.CacheResumeThresholdGb.ToString();
            _minimumAvailableMemoryGb = copy.MinimumAvailableMemoryGb.ToString();
            _criticalAvailableMemoryGb = copy.CriticalAvailableMemoryGb.ToString();
            _allowExistingSearchIndexesBelowMemoryLimit = copy.AllowExistingSearchIndexesBelowMemoryLimit;
            _maxCachedModelSearchServices = copy.MaxCachedModelSearchServices.ToString();
            _maxCachedPartSearchServices = copy.MaxCachedPartSearchServices.ToString();
            _maxSearchResults = copy.MaxSearchResults.ToString();
            _maxPropertyElements = copy.MaxPropertyElements.ToString();
            _maxSpatialTraversalNodes = copy.MaxSpatialTraversalNodes.ToString();
            _maxSpatialResultMiB = ((copy.MaxSpatialResultBytes+1048575)/1048576).ToString();
        }

        private void LoadTimeouts(BimBridgeSettings value)
        {
            _queueTimeout = value.SdkDispatcherQueueTimeoutSeconds.ToString();
            _initializationTimeout = value.SearchServiceInitializationTimeoutSeconds.ToString();
            _cleanupTimeout = value.SdkCleanupTimeoutSeconds.ToString();
        }

        private void Set(ref string field, string value, [CallerMemberName] string propertyName = null)
        {
            value = value ?? string.Empty;
            if (string.Equals(field, value, StringComparison.Ordinal)) return;
            field = value;
            OnPropertyChanged(propertyName);
            OnPropertyChanged(nameof(Error));
            OnPropertyChanged(nameof(HasErrors));
            CommandManager.InvalidateRequerySuggested();
        }

        private static string ValidateRange(string source, int minimum, int maximum, string name)
        {
            int value;
            if (!TryRead(source, out value)) return "Введите целое число.";
            return value < minimum || value > maximum
                ? string.Format("{0}: допустимо от {1} до {2}.", name, minimum, maximum)
                : null;
        }

        private static int Read(string source)
        {
            int value;
            if (!TryRead(source, out value)) throw new InvalidOperationException("Resource limit is not a valid integer.");
            return value;
        }

        private static bool TryRead(string source, out int value) =>
            int.TryParse(source, out value);

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
