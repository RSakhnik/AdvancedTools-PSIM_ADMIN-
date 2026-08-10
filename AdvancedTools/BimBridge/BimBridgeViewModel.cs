using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AdvancedTools.BimBridge
{
    public sealed class BimBridgeViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly BimBridgeWorkerController _controller;
        private readonly Dispatcher _uiDispatcher;
        private readonly CoalescingSignal _snapshotSignal;
        private readonly CoalescingSignal _catalogSignal;
        private readonly CoalescingSignal _resourceSignal;
        private BimBridgeSnapshot _snapshot;
        private CatalogProgressSnapshot _catalogProgress =
            new CatalogProgressSnapshot(false, 0, 0, 0, "Каталог ещё не обновлялся");
        private string[] _models = Array.Empty<string>();
        private ICollectionView _filteredModels;
        private string _resourceStatus = "Обновление данных...";
        private string _modelFilter = string.Empty;
        private string _newProjectName = string.Empty;
        private string _projectSelectionStatus = "Выберите проекты, модели которых должен обслуживать работник";
        private int _snapshotDispatchPending;
        private int _catalogRefreshRunning;
        private int _catalogRefreshRequested;
        private int _resourceRefreshRunning;
        private bool _disposed;

        public BimBridgeViewModel(BimBridgeWorkerController controller, BimBridgeSettings settings)
        {
            _controller = controller;
            Settings = settings;
            _snapshot = controller.State.Current;
            SelectedProjects = new ObservableCollection<string>(settings.GetLoadedProjectNames());
            _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            ReplaceModels(Array.Empty<string>());
            _snapshotSignal = new CoalescingSignal(TimeSpan.FromMilliseconds(75), QueueSnapshotToUi);
            _catalogSignal = new CoalescingSignal(TimeSpan.FromMilliseconds(250), ScheduleCatalogRefresh);
            _resourceSignal = new CoalescingSignal(TimeSpan.FromSeconds(1), ScheduleResourceRefresh);
            RegisterCommand = new AsyncRelayCommand(_controller.RegisterAsync, () => !IsRegistered && !IsRunning);
            StartCommand = new AsyncRelayCommand(_controller.StartAsync, () => IsRegistered && !IsRunning);
            StopCommand = new AsyncRelayCommand(_controller.StopAsync, () => IsRunning);
            UnregisterCommand = new AsyncRelayCommand(_controller.UnregisterAsync, () => IsRegistered);
            RefreshCommand = new AsyncRelayCommand(_controller.RefreshAsync, () => true);
            SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => true);
            AddProjectCommand = new RelayCommand(AddProject, CanAddProject);
            RemoveProjectCommand = new ParameterRelayCommand(RemoveProject, CanRemoveProject);
            ApplyProjectsCommand = new AsyncRelayCommand(ApplyProjectSelectionAsync, () => SelectedProjects.Count > 0);
            CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
            controller.State.SnapshotChanged += OnSnapshotChanged;
            controller.CatalogDisplayChanged += OnCatalogDisplayChanged;
            _catalogSignal.Request();
            _resourceSignal.Request();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public BimBridgeSettings Settings { get; }
        public IReadOnlyList<string> Models => _models;
        public ICollectionView FilteredModels => _filteredModels;
        public ICommand RegisterCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand UnregisterCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand AddProjectCommand { get; }
        public ICommand RemoveProjectCommand { get; }
        public ICommand ApplyProjectsCommand { get; }
        public ICommand CopyDiagnosticsCommand { get; }
        public ObservableCollection<string> SelectedProjects { get; }

        public string StateDisplay
        {
            get
            {
                switch (_snapshot.State)
                {
                    case BimBridgeClientStates.Connecting: return "Подключение";
                    case BimBridgeClientStates.Registered: return "Зарегистрирован";
                    case BimBridgeClientStates.Waiting: return "Ожидает задания";
                    case BimBridgeClientStates.Executing: return "Выполняет задание";
                    case BimBridgeClientStates.Uploading: return "Отправляет результат";
                    case BimBridgeClientStates.Reconnecting: return "Переподключение";
                    case BimBridgeClientStates.Error: return "Ошибка";
                    case BimBridgeClientStates.Unsupported: return "Несовместимая версия";
                    default: return "Остановлен";
                }
            }
        }

        public Brush StateBrush
        {
            get
            {
                switch (_snapshot.State)
                {
                    case BimBridgeClientStates.Waiting:
                    case BimBridgeClientStates.Registered: return Brushes.SeaGreen;
                    case BimBridgeClientStates.Executing:
                    case BimBridgeClientStates.Uploading:
                    case BimBridgeClientStates.Connecting: return Brushes.RoyalBlue;
                    case BimBridgeClientStates.Reconnecting: return Brushes.DarkOrange;
                    case BimBridgeClientStates.Error:
                    case BimBridgeClientStates.Unsupported: return Brushes.IndianRed;
                    default: return Brushes.SlateGray;
                }
            }
        }

        public string WorkerInstanceId => string.IsNullOrWhiteSpace(_snapshot.WorkerInstanceId) ? "—" : _snapshot.WorkerInstanceId;
        public string RegistrationId => _snapshot.RegistrationId == Guid.Empty ? "—" : _snapshot.RegistrationId.ToString("D");
        public string CurrentTaskId => _snapshot.CurrentTaskId?.ToString("D") ?? "Нет активного задания";
        public string CurrentOperation => LocalizeOperation(_snapshot.CurrentOperation);
        public int ProgressPercent => HasActiveTaskProgress ? _snapshot.ProgressPercent : _catalogProgress.Percent;
        public bool IsProgressIndeterminate => !HasActiveTaskProgress && _catalogProgress.IsIndeterminate;
        public string ProgressDescription
        {
            get
            {
                if (HasActiveTaskProgress) return LocalizeOperation(_snapshot.CurrentOperation);
                if (_catalogProgress.Total > 0)
                    return string.Format(
                        "{0}: {1} из {2}",
                        _catalogProgress.Description,
                        _catalogProgress.Completed,
                        _catalogProgress.Total);
                return _catalogProgress.Description;
            }
        }
        public string LastHeartbeat => _snapshot.LastHeartbeatUtc?.ToLocalTime().ToString("G") ?? "Ещё не выполнялся";
        public string LastError => string.IsNullOrWhiteSpace(_snapshot.LastError)
            ? "Ошибок нет"
            : string.Format("{0}: {1}", _snapshot.LastErrorCode, _snapshot.LastError);
        public Brush ErrorBrush => string.IsNullOrWhiteSpace(_snapshot.LastError) ? Brushes.SeaGreen : Brushes.IndianRed;
        public string Capabilities => string.Join(", ", _snapshot.Capabilities.Select(LocalizeCapability));
        public string ResourceStatus => _resourceStatus;
        public bool IsRunning => _snapshot.IsRunning;
        public bool IsRegistered => _snapshot.IsRegistered;
        public string ModelsSummary => string.Format("Строк в каталоге: {0}", _models.Length);
        public string SelectedProjectsSummary => string.Format("Выбрано проектов: {0}", SelectedProjects.Count);
        public string ProjectSelectionStatus
        {
            get => _projectSelectionStatus;
            private set
            {
                if (string.Equals(_projectSelectionStatus, value, StringComparison.Ordinal)) return;
                _projectSelectionStatus = value;
                OnPropertyChanged();
            }
        }

        public string NewProjectName
        {
            get => _newProjectName;
            set
            {
                if (string.Equals(_newProjectName, value, StringComparison.Ordinal)) return;
                _newProjectName = value ?? string.Empty;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }
        private bool HasActiveTaskProgress =>
            _snapshot.CurrentTaskId.HasValue ||
            string.Equals(_snapshot.State, BimBridgeClientStates.Executing, StringComparison.Ordinal) ||
            string.Equals(_snapshot.State, BimBridgeClientStates.Uploading, StringComparison.Ordinal);

        public string ModelFilter
        {
            get => _modelFilter;
            set
            {
                if (string.Equals(_modelFilter, value, StringComparison.Ordinal)) return;
                _modelFilter = value ?? string.Empty;
                OnPropertyChanged();
                _filteredModels?.Refresh();
            }
        }

        private bool FilterModel(object value)
        {
            if (string.IsNullOrWhiteSpace(_modelFilter)) return true;
            return (value as string ?? string.Empty)
                .IndexOf(_modelFilter.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void OnSnapshotChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            _snapshotSignal.Request();
            _resourceSignal.Request();
        }

        private void OnCatalogDisplayChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            _catalogSignal.Request();
        }

        private void QueueSnapshotToUi()
        {
            if (_disposed || Interlocked.CompareExchange(ref _snapshotDispatchPending, 1, 0) != 0) return;
            try
            {
                _uiDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_disposed) return;
                        _snapshot = _controller.State.Current;
                        OnPropertyChanged(string.Empty);
                        CommandManager.InvalidateRequerySuggested();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _snapshotDispatchPending, 0);
                        if (!_disposed && !ReferenceEquals(_snapshot, _controller.State.Current))
                            _snapshotSignal.Request();
                    }
                }), DispatcherPriority.Background);
            }
            catch
            {
                Interlocked.Exchange(ref _snapshotDispatchPending, 0);
            }
        }

        private void ScheduleCatalogRefresh()
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _catalogRefreshRequested, 1);
            if (Interlocked.CompareExchange(ref _catalogRefreshRunning, 1, 0) == 0)
                _ = RefreshCatalogAsync();
        }

        private async Task RefreshCatalogAsync()
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _catalogRefreshRequested, 0);
                    if (_disposed) return;
                    var items = _controller.GetModelDisplayItems();
                    var resourceStatus = _controller.GetResourceStatus();
                    var catalogProgress = _controller.GetCatalogProgress();
                    await InvokeOnUiBackgroundAsync(() =>
                    {
                        if (_disposed) return;
                        if (!_models.SequenceEqual(items)) ReplaceModels(items);
                        SetResourceStatus(resourceStatus);
                        SetCatalogProgress(catalogProgress);
                    }).ConfigureAwait(false);
                }
                while (!_disposed && Interlocked.Exchange(ref _catalogRefreshRequested, 0) != 0);
            }
            catch
            {
                // Closing Pilot or the window can stop the dispatcher while a
                // background catalog refresh is waiting.  The worker remains safe.
            }
            finally
            {
                Interlocked.Exchange(ref _catalogRefreshRunning, 0);
                if (!_disposed && Volatile.Read(ref _catalogRefreshRequested) != 0)
                    ScheduleCatalogRefresh();
            }
        }

        private void ScheduleResourceRefresh()
        {
            if (_disposed || Interlocked.CompareExchange(ref _resourceRefreshRunning, 1, 0) != 0) return;
            _ = RefreshResourceAsync();
        }

        private async Task RefreshResourceAsync()
        {
            try
            {
                var value = _controller.GetResourceStatus();
                await InvokeOnUiBackgroundAsync(() => SetResourceStatus(value)).ConfigureAwait(false);
            }
            catch { }
            finally { Interlocked.Exchange(ref _resourceRefreshRunning, 0); }
        }

        private Task InvokeOnUiBackgroundAsync(Action action)
        {
            if (_disposed || _uiDispatcher.HasShutdownStarted || _uiDispatcher.HasShutdownFinished)
                return Task.CompletedTask;
            if (_uiDispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }
            return _uiDispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
        }

        private void ReplaceModels(string[] items)
        {
            _models = items ?? Array.Empty<string>();
            _filteredModels = CollectionViewSource.GetDefaultView(_models);
            _filteredModels.Filter = FilterModel;
            OnPropertyChanged(nameof(Models));
            OnPropertyChanged(nameof(FilteredModels));
            OnPropertyChanged(nameof(ModelsSummary));
        }

        private void SetResourceStatus(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "Данные о ресурсах пока недоступны" : value;
            if (string.Equals(_resourceStatus, value, StringComparison.Ordinal)) return;
            _resourceStatus = value;
            OnPropertyChanged(nameof(ResourceStatus));
        }

        private void SetCatalogProgress(CatalogProgressSnapshot value)
        {
            if (value == null) return;
            if (_catalogProgress.IsRunning == value.IsRunning &&
                _catalogProgress.Completed == value.Completed &&
                _catalogProgress.Total == value.Total &&
                _catalogProgress.Percent == value.Percent &&
                string.Equals(_catalogProgress.Description, value.Description, StringComparison.Ordinal)) return;
            _catalogProgress = value;
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
            OnPropertyChanged(nameof(ProgressDescription));
        }

        private void CopyDiagnostics()
        {
            try { Clipboard.SetText(_controller.GetDiagnostics()); }
            catch { }
        }

        private bool CanAddProject()
        {
            var name = (_newProjectName ?? string.Empty).Trim();
            return name.Length > 0 && !SelectedProjects.Any(x =>
                string.Equals(x, name, StringComparison.CurrentCultureIgnoreCase));
        }

        private void AddProject()
        {
            var name = (_newProjectName ?? string.Empty).Trim();
            if (name.Length == 0 || SelectedProjects.Any(x =>
                    string.Equals(x, name, StringComparison.CurrentCultureIgnoreCase))) return;
            SelectedProjects.Add(name);
            NewProjectName = string.Empty;
            MarkProjectSelectionChanged();
        }

        private bool CanRemoveProject(object parameter)
        {
            return SelectedProjects.Count > 1 && parameter is string;
        }

        private void RemoveProject(object parameter)
        {
            var name = parameter as string;
            if (name == null || SelectedProjects.Count <= 1) return;
            SelectedProjects.Remove(name);
            MarkProjectSelectionChanged();
        }

        private void MarkProjectSelectionChanged()
        {
            ProjectSelectionStatus = "Есть неприменённые изменения";
            OnPropertyChanged(nameof(SelectedProjectsSummary));
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task ApplyProjectSelectionAsync()
        {
            if (SelectedProjects.Count == 0) return;
            Settings.LoadedProjectNames = string.Join("; ", SelectedProjects);
            Settings.Save();
            ProjectSelectionStatus = "Обновление каталога…";
            try
            {
                await _controller.RefreshAsync();
                ProjectSelectionStatus = "Применено — каталог обновлён";
            }
            catch
            {
                ProjectSelectionStatus = "Не удалось обновить каталог — подробности в диагностике";
                throw;
            }
        }

        private async Task SaveSettingsAsync()
        {
            Settings.LoadedProjectNames = string.Join("; ", SelectedProjects);
            Settings.Save();
            await _controller.RefreshAsync().ConfigureAwait(false);
        }

        private static string LocalizeOperation(string operation)
        {
            if (string.IsNullOrWhiteSpace(operation)) return "Нет активной операции";
            switch (operation)
            {
                case "get-model-context":
                case "model-context": return "Получение каталога моделей";
                case "search-elements": return "Поиск элементов";
                case "load-element-properties": return "Получение свойств элементов";
                case "download-pilot-file": return "Выгрузка файла Pilot";
                case "read-excel-content": return "Чтение Excel";
                default: return operation;
            }
        }

        private static string LocalizeCapability(string capability)
        {
            switch (capability)
            {
                case "model-context": return "каталог моделей";
                case "search-elements": return "поиск элементов";
                case "load-element-properties": return "свойства элементов";
                case "download-pilot-file": return "выгрузка файлов";
                case "read-excel-content": return "чтение Excel";
                case "resolve-model-key": return "разрешение GUID моделей";
                case "diagnostics": return "диагностика";
                default: return capability;
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _controller.State.SnapshotChanged -= OnSnapshotChanged;
            _controller.CatalogDisplayChanged -= OnCatalogDisplayChanged;
            _snapshotSignal.Dispose();
            _catalogSignal.Dispose();
            _resourceSignal.Dispose();
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;
        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }
        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object parameter) => _execute();
        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }

    public sealed class ParameterRelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public ParameterRelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object parameter) => _execute(parameter);
        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }

    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;
        private bool _executing;

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => !_executing && (_canExecute?.Invoke() ?? true);

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) return;
            _executing = true;
            CommandManager.InvalidateRequerySuggested();
            try { await _execute(); }
            catch { }
            finally
            {
                _executing = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}
