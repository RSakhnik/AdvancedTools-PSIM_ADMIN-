using Ascon.Pilot.Bim.SDK.Model;
using Ascon.Pilot.Bim.SDK.ModelStorage;
using Ascon.Pilot.Bim.SDK.Search;
using Ascon.Pilot.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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
        private bool _initializationStarted;
        private bool _exitSubscribed;
        private bool _disposed;

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
        }

        public void ShowWindow()
        {
            if (_disposed) return;
            _log.Info("BIM Bridge window requested.");
            if (_window == null)
            {
                _window = new BimBridgeWindow();
                _log.Info("BIM Bridge window constructed.");
            }

            if (_window.IsVisible)
            {
                if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
                _window.Activate();
            }
            else
            {
                _window.Show();
                _window.Activate();
            }
            _log.Info("BIM Bridge window shown.");

            if (!_initializationStarted && _controller == null)
            {
                _window.ShowInitialization("Инициализация BIM Bridge…");
                _window.Dispatcher.BeginInvoke(
                    new Action(InitializeAfterWindowShown),
                    DispatcherPriority.Background);
                _log.Info("BIM Bridge initialization queued.");
            }
        }

        private void InitializeAfterWindowShown()
        {
            if (_disposed || _initializationStarted || _controller != null) return;
            _initializationStarted = true;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _log.Info("BIM Bridge UI initialization started.");
                _window.ShowInitialization("Загрузка настроек…");
                _settings = BimBridgeSettings.Load();

                _window.ShowInitialization("Подключение BIM-сервисов…");
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

                _window.ShowInitialization("Подготовка обработчиков заданий…");
                var handlers = new List<IBimOperationHandler>
                {
                    new ModelContextOperationHandler(_models),
                    new SearchElementsOperationHandler(_models, searchManager, _settings),
                    new LoadElementPropertiesOperationHandler(_models, _settings),
                    new DownloadPilotFileOperationHandler(new PilotFileOperations(_objectsRepository, fileProvider, _settings, _log)),
                    new ReadExcelContentOperationHandler(new PilotFileOperations(_objectsRepository, fileProvider, _settings, _log)),
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
                    personId);

                _viewModel = new BimBridgeViewModel(_controller, _settings);
                _window.SetViewModel(_viewModel);
                SubscribeToApplicationExit();
                _log.Info($"BIM Bridge UI initialized in {stopwatch.ElapsedMilliseconds} ms.");

                if (_settings.AutoStart)
                    _ = StartSilentlyAsync();
            }
            catch (Exception exception)
            {
                _log.Error("BIM Bridge UI initialization failed.", exception);
                _window.ShowInitializationError(exception.Message);
                _initializationStarted = false;
                _controller?.Dispose();
                _controller = null;
                _models?.Dispose();
                _models = null;
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
            _viewModel?.Dispose();
            if (_window != null)
            {
                try { _window.ClosePermanently(); }
                catch { }
            }
            _controller?.Dispose();
            _models?.Dispose();
        }
    }
}
