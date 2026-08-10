using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace AdvancedTools.BimBridge
{
    public sealed class BimBridgeViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly BimBridgeWorkerController _controller;
        private BimBridgeSnapshot _snapshot;

        public BimBridgeViewModel(BimBridgeWorkerController controller, BimBridgeSettings settings)
        {
            _controller = controller;
            Settings = settings;
            _snapshot = controller.State.Current;
            Models = new ObservableCollection<string>(_controller.GetModelDisplayItems());
            RegisterCommand = new AsyncRelayCommand(_controller.RegisterAsync, () => !IsRegistered && !IsRunning);
            StartCommand = new AsyncRelayCommand(_controller.StartAsync, () => IsRegistered && !IsRunning);
            StopCommand = new AsyncRelayCommand(_controller.StopAsync, () => IsRunning);
            UnregisterCommand = new AsyncRelayCommand(_controller.UnregisterAsync, () => IsRegistered);
            RefreshCommand = new AsyncRelayCommand(_controller.RefreshAsync, () => true);
            SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => true);
            CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
            controller.State.SnapshotChanged += OnSnapshotChanged;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public BimBridgeSettings Settings { get; }
        public ObservableCollection<string> Models { get; }
        public ICommand RegisterCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand UnregisterCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand CopyDiagnosticsCommand { get; }

        public string State => _snapshot.State;
        public string WorkerInstanceId => _snapshot.WorkerInstanceId;
        public string RegistrationId => _snapshot.RegistrationId == Guid.Empty ? "—" : _snapshot.RegistrationId.ToString("D");
        public string CurrentTaskId => _snapshot.CurrentTaskId?.ToString("D") ?? "—";
        public string CurrentOperation => string.IsNullOrWhiteSpace(_snapshot.CurrentOperation) ? "—" : _snapshot.CurrentOperation;
        public int ProgressPercent => _snapshot.ProgressPercent;
        public string LastHeartbeat => _snapshot.LastHeartbeatUtc?.ToLocalTime().ToString("G") ?? "—";
        public string LastError => string.IsNullOrWhiteSpace(_snapshot.LastError)
            ? "—"
            : $"{_snapshot.LastErrorCode}: {_snapshot.LastError}";
        public string Capabilities => string.Join(", ", _snapshot.Capabilities);
        public bool IsRunning => _snapshot.IsRunning;
        public bool IsRegistered => _snapshot.IsRegistered;

        private void OnSnapshotChanged(object sender, EventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ApplySnapshot));
                return;
            }
            ApplySnapshot();
        }

        private void ApplySnapshot()
        {
            _snapshot = _controller.State.Current;
            Models.Clear();
            foreach (var model in _controller.GetModelDisplayItems()) Models.Add(model);
            OnPropertyChanged(string.Empty);
            CommandManager.InvalidateRequerySuggested();
        }

        private void CopyDiagnostics()
        {
            try { Clipboard.SetText(_controller.GetDiagnostics()); }
            catch { }
        }

        private async Task SaveSettingsAsync()
        {
            Settings.Save();
            await _controller.RefreshAsync().ConfigureAwait(false);
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public void Dispose()
        {
            _controller.State.SnapshotChanged -= OnSnapshotChanged;
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
