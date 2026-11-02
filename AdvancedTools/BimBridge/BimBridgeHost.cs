using Ascon.Pilot.Bim.SDK.Model;
using Ascon.Pilot.Bim.SDK.ModelStorage;
using Ascon.Pilot.Bim.SDK.Search;
using Ascon.Pilot.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AdvancedTools.BimBridge
{
    public sealed class BimBridgeHost : IDisposable
    {
        private readonly IPilotServiceProvider _serviceProvider;
        private readonly IObjectsRepository _objectsRepository;
        private readonly ISearchService _pilotSearchService;
        private readonly ITabServiceProvider _tabServiceProvider;
        private readonly IFileProvider _fileProvider;
        private readonly IBimBridgeLog _log;
        private BimBridgeSettings _settings;
        private ModelContextProvider _models;
        private BimBridgeWorkerController _controller;
        private BimBridgeViewModel _viewModel;
        private BimBridgeWindow _window;
        private WorkerTemporaryWorkspace _workspace;
        private bool _initializationStarted;
        private bool _exitSubscribed;
        private bool _disposed;
        private readonly Dispatcher _pilotDispatcher;
        private Thread _windowThread;
        private readonly object _windowGate = new object();

        public BimBridgeHost(
            IPilotServiceProvider serviceProvider,
            IObjectsRepository objectsRepository,
            ISearchService pilotSearchService,
            ITabServiceProvider tabServiceProvider,
            IFileProvider fileProvider = null)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _objectsRepository = objectsRepository ?? throw new ArgumentNullException(nameof(objectsRepository));
            _pilotSearchService = pilotSearchService;
            _tabServiceProvider = tabServiceProvider;
            _fileProvider = fileProvider;
            _log = new FileBimBridgeLog();
            _pilotDispatcher = Dispatcher.CurrentDispatcher;
        }

        public void ShowWindow()
        {
            if (_disposed) return;
            _log.Info("BIM Bridge window requested.");
            lock (_windowGate)
            {
                if (_windowThread == null)
                {
                    _windowThread = new Thread(() =>
                    {
                        try
                        {
                            _window = new BimBridgeWindow();
                            _window.ShowInitialization("Инициализация BIM Bridge…");
                            _window.Show();
                            _pilotDispatcher.BeginInvoke(new Action(InitializeAfterWindowShown), DispatcherPriority.Background);
                            Dispatcher.Run();
                        }
                        catch (Exception error) { _log.Error("Independent worker UI failed.", error); }
                    }) { IsBackground = true, Name = "BIM Bridge UI" };
                    _windowThread.SetApartmentState(ApartmentState.STA);
                    _windowThread.Start();
                    return;
                }
            }
            OnWindow(window =>
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Show(); window.Activate();
            });
            if (!_initializationStarted && _controller == null)
                _pilotDispatcher.BeginInvoke(new Action(InitializeAfterWindowShown), DispatcherPriority.Background);
        }

        private void OnWindow(Action<BimBridgeWindow> action)
        {
            var window = _window;
            if (window != null && !window.Dispatcher.HasShutdownStarted)
                window.Dispatcher.BeginInvoke(new Action(() => action(window)));
        }

        private void InitializeAfterWindowShown()
        {
            if (_disposed || _initializationStarted || _controller != null) return;
            _initializationStarted = true;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _log.Info("BIM Bridge UI initialization started.");
                OnWindow(window => window.ShowInitialization("Загрузка настроек…"));
                _settings = BimBridgeSettings.Load();
                _workspace = new WorkerTemporaryWorkspace(_settings, _log);

                OnWindow(window => window.ShowInitialization("Подключение BIM-сервисов…"));
                var modelManager = _serviceProvider.GetServices<IModelManager>().FirstOrDefault();
                var storageProvider = _serviceProvider.GetServices<IModelStorageProvider>().FirstOrDefault();
                var searchManager = _serviceProvider.GetServices<IModelSearchManager>().FirstOrDefault();
                var fileProvider = _fileProvider ?? _serviceProvider.GetServices<IFileProvider>().FirstOrDefault();
                if (fileProvider == null)
                    _log.Warn("IFileProvider is unavailable; Pilot file operations will not be registered.");
                _models = new ModelContextProvider(
                    _objectsRepository,
                    _pilotSearchService,
                    modelManager,
                    storageProvider,
                    searchManager,
                    _tabServiceProvider,
                    _settings,
                    _log);

                OnWindow(window => window.ShowInitialization("Подготовка обработчиков заданий…"));
                var resultWriter = new ResultFileWriter(_settings, _workspace);
                var fileOperations = new PilotFileOperations(
                    _objectsRepository,
                    fileProvider,
                    _settings,
                    _log,
                    _workspace);
                var handlers = new List<IBimOperationHandler>
                {
                    new ModelContextOperationHandler(_models),
                    new ExportElementsOperationHandler(_models, _settings, new ArtifactFileWriter(resultWriter.TemporaryDirectory, _settings)),
                    new ExportSpatialOperationHandler(_models, _settings, new ArtifactFileWriter(resultWriter.TemporaryDirectory, _settings), resultWriter.TemporaryDirectory),
                    new SearchElementsOperationHandler(_models, searchManager, _settings, resultWriter,
                        new PagedSearch(_models, _settings, Environment.MachineName + ":" + (_objectsRepository.GetCurrentPerson()?.Id ?? 0) + ":" + _settings.DurableWorkerId)),
                    new LoadElementPropertiesOperationHandler(_models, _settings, resultWriter),
                    new GetElementRelationsOperationHandler(_models, _settings, resultWriter),
                    new GetElementSpatialDataOperationHandler(_models, _settings, resultWriter),
                    new DiagnoseElementSpatialDataOperationHandler(_models, _settings, resultWriter),
                    new DownloadPilotFileOperationHandler(fileOperations),
                    new ReadExcelContentOperationHandler(fileOperations),
                    new DiagnosticOperationHandler(_settings.DiagnosticOperationsEnabled)
                };
                var operationDispatcher = new BimOperationDispatcher(handlers);
                var transport = new PilotSessionCustomApiProvider(_objectsRepository, _log);
                var personId = _objectsRepository.GetCurrentPerson()?.Id ?? 0;
                _controller = new BimBridgeWorkerController(
                    () => transport.CreateClient(),
                    operationDispatcher,
                    _models,
                    _settings,
                    _log,
                    personId,
                    resultWriter,
                    _workspace);

                OnWindow(window =>
                {
                    _viewModel = new BimBridgeViewModel(_controller, _settings, window.Dispatcher);
                    window.SetViewModel(_viewModel);
                });
                SubscribeToApplicationExit();
                _log.Info($"BIM Bridge UI initialized in {stopwatch.ElapsedMilliseconds} ms.");

                if (_settings.AutoStart)
                    _ = StartSilentlyAsync();
            }
            catch (Exception exception)
            {
                _log.Error("BIM Bridge UI initialization failed.", exception);
                OnWindow(window => window.ShowInitializationError(exception.Message));
                _initializationStarted = false;
                _controller?.Dispose();
                _controller = null;
                _models?.Dispose();
                _models = null;
                _workspace?.Dispose();
                _workspace = null;
            }
        }

        private void SubscribeToApplicationExit()
        {
            if (_exitSubscribed || Application.Current == null) return;
            Application.Current.Exit += OnApplicationExit;
            _exitSubscribed = true;
        }

        private async Task StartSilentlyAsync()
        {
            try { await _controller.StartAsync(); }
            catch { }
        }

        private void OnApplicationExit(object sender, ExitEventArgs e) => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_exitSubscribed && Application.Current != null)
                Application.Current.Exit -= OnApplicationExit;
            _exitSubscribed = false;
            OnWindow(window =>
            {
                _viewModel?.Dispose();
                window.ClosePermanently();
                window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            });
            _controller?.Dispose();
            _ = Task.Run(async () =>
            {
                if (_controller != null) await _controller.ShutdownCompletion.ConfigureAwait(false);
                if (_models != null) await _models.GetPendingSdkCleanup().ConfigureAwait(false);
                _ = _pilotDispatcher.BeginInvoke(new Action(() => _models?.Dispose()), DispatcherPriority.Background);
                _workspace?.Dispose();
            });
        }
    }
}
