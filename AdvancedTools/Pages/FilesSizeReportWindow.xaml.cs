using Ascon.Pilot.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PilotDataObject = Ascon.Pilot.SDK.IDataObject;

namespace AdvancedTools.Pages
{
    public partial class FilesSizeReportWindow : Window
    {
        private const int SubscribeChunkSize = 200;
        private const int SubscribeTimeoutSeconds = 20;

        private readonly IObjectsRepository _objectsRepository;
        private readonly List<PilotObjectSnapshot> _initialRootSnapshots;

        private CancellationTokenSource _loadCancellation;
        private PilotSizeNode _rootNode;
        private PilotSizeNode _currentNode;

        public FilesSizeReportWindow(IObjectsRepository objectsRepository)
            : this(objectsRepository, null)
        {
        }

        public FilesSizeReportWindow(IObjectsRepository objectsRepository, IEnumerable<PilotDataObject> rootObjects)
        {
            _objectsRepository = objectsRepository;
            _initialRootSnapshots = CopyInitialRootSnapshots(rootObjects);

            InitializeComponent();
            Closed += FilesSizeReportWindow_Closed;

            if (_initialRootSnapshots.Count > 0)
            {
                EmptyChartTextBlock.Text = _initialRootSnapshots.Count == 1
                    ? "Нажмите «Построить из SDK», чтобы просканировать выбранный объект."
                    : "Нажмите «Построить из SDK», чтобы просканировать выбранные объекты.";
            }
        }

        private async void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadFromSdkAsync();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loadCancellation != null)
                _loadCancellation.Cancel();
        }

        private void FilesSizeReportWindow_Closed(object sender, EventArgs e)
        {
            if (_loadCancellation != null)
                _loadCancellation.Cancel();
        }

        private async Task LoadFromSdkAsync()
        {
            if (_objectsRepository == null)
            {
                MessageBox.Show(this, "SDK IObjectsRepository недоступен.", "Размер файлов БД",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_loadCancellation != null)
                _loadCancellation.Cancel();

            _loadCancellation = new CancellationTokenSource();
            var token = _loadCancellation.Token;

            ToggleUi(false);
            ResetViewForLoading();

            try
            {
                var progress = new Progress<ScanProgress>(UpdateScanProgress);
                var report = await Task.Run(() =>
                {
                    var builder = new SdkTreeBuilder(_objectsRepository);
                    return builder.Build(_initialRootSnapshots, progress, token);
                }, token);

                token.ThrowIfCancellationRequested();

                _rootNode = report.Root;
                _currentNode = _rootNode;

                RootTextBlock.Text = _rootNode.DisplayName;
                SummaryTextBlock.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "Объектов: {0:N0}; файлов: {1:N0}; общий размер: {2}",
                    report.ObjectCount,
                    _rootNode.TotalFileCount,
                    SizeFormatter.Format(_rootNode.TotalSizeBytes));

                StatusTextBlock.Text = report.SkippedObjectCount == 0
                    ? "Готово."
                    : string.Format(CultureInfo.InvariantCulture, "Готово. Не удалось загрузить объектов: {0:N0}.", report.SkippedObjectCount);

                RenderCurrentNode();
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = "Сканирование отменено.";
            }
            catch (Exception ex)
            {
                _rootNode = null;
                _currentNode = null;
                ChartItemsControl.ItemsSource = null;
                DetailsDataGrid.ItemsSource = null;
                BreadcrumbPanel.Children.Clear();
                EmptyChartTextBlock.Text = ex.Message;
                EmptyChartTextBlock.Visibility = Visibility.Visible;
                ChartScrollViewer.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = "Ошибка.";

                MessageBox.Show(this, ex.Message, "Ошибка сканирования SDK",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 0;
                ToggleUi(true);
            }
        }

        private void ToggleUi(bool isReady)
        {
            LoadButton.IsEnabled = isReady;
            CancelButton.IsEnabled = !isReady;
        }

        private void ResetViewForLoading()
        {
            ProgressBar.IsIndeterminate = true;
            ProgressBar.Value = 0;
            RootTextBlock.Text = "";
            SummaryTextBlock.Text = "";
            StatusTextBlock.Text = _initialRootSnapshots.Count > 0
                ? "Подготовка выбранных объектов..."
                : "Получение корня Pilot...";
            EmptyChartTextBlock.Text = "Сканирование дерева Pilot...";
            EmptyChartTextBlock.Visibility = Visibility.Visible;
            ChartScrollViewer.Visibility = Visibility.Collapsed;
            ChartItemsControl.ItemsSource = null;
            DetailsDataGrid.ItemsSource = null;
            BreadcrumbPanel.Children.Clear();
        }

        private void UpdateScanProgress(ScanProgress progress)
        {
            StatusTextBlock.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Сканирование: объектов {0:N0}, файлов {1:N0}, текущий узел: {2}",
                progress.ObjectCount,
                progress.FileCount,
                progress.CurrentName);
        }

        private void RenderCurrentNode()
        {
            if (_currentNode == null)
            {
                ChartItemsControl.ItemsSource = null;
                DetailsDataGrid.ItemsSource = null;
                BreadcrumbPanel.Children.Clear();
                EmptyChartTextBlock.Visibility = Visibility.Visible;
                ChartScrollViewer.Visibility = Visibility.Collapsed;
                return;
            }

            var children = _currentNode.Children
                .OrderByDescending(node => node.TotalSizeBytes)
                .ThenBy(node => node.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (children.Count == 0)
            {
                ChartItemsControl.ItemsSource = null;
                DetailsDataGrid.ItemsSource = null;
                EmptyChartTextBlock.Text = "У выбранного узла нет дочерних объектов или файлов.";
                EmptyChartTextBlock.Visibility = Visibility.Visible;
                ChartScrollViewer.Visibility = Visibility.Collapsed;
            }
            else
            {
                var maxSize = Math.Max(1, children.Max(node => node.TotalSizeBytes));
                var bars = children.Select(node => new ChartBarViewModel(node, maxSize)).ToList();
                ChartItemsControl.ItemsSource = bars;
                DetailsDataGrid.ItemsSource = bars;
                EmptyChartTextBlock.Visibility = Visibility.Collapsed;
                ChartScrollViewer.Visibility = Visibility.Visible;
            }

            RenderBreadcrumb();
        }

        private void RenderBreadcrumb()
        {
            BreadcrumbPanel.Children.Clear();

            var chain = new List<PilotSizeNode>();
            var current = _currentNode;
            while (current != null)
            {
                chain.Add(current);
                current = current.Parent;
            }

            chain.Reverse();

            for (var i = 0; i < chain.Count; i++)
            {
                var node = chain[i];
                var button = new Button
                {
                    Content = i == 0 ? node.DisplayName : TrimText(node.DisplayName, 42),
                    Tag = node,
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                    ToolTip = node.PathText
                };

                button.Click += BreadcrumbButton_Click;
                BreadcrumbPanel.Children.Add(button);

                if (i < chain.Count - 1)
                {
                    BreadcrumbPanel.Children.Add(new TextBlock
                    {
                        Text = ">",
                        Margin = new Thickness(6, 3, 2, 0),
                        Foreground = Brushes.Gray
                    });
                }
            }
        }

        private void BreadcrumbButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var node = button == null ? null : button.Tag as PilotSizeNode;
            if (node == null)
                return;

            _currentNode = node;
            RenderCurrentNode();
        }

        private void BarButton_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var bar = element == null ? null : element.DataContext as ChartBarViewModel;
            if (bar == null || !bar.Node.HasChildren)
                return;

            _currentNode = bar.Node;
            RenderCurrentNode();
        }

        private void DetailsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var bar = DetailsDataGrid.SelectedItem as ChartBarViewModel;
            if (bar == null || !bar.Node.HasChildren)
                return;

            _currentNode = bar.Node;
            RenderCurrentNode();
        }

        private static string TrimText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            return text.Substring(0, maxLength - 1) + "...";
        }

        private static List<PilotObjectSnapshot> CopyInitialRootSnapshots(IEnumerable<PilotDataObject> rootObjects)
        {
            var snapshots = new List<PilotObjectSnapshot>();
            if (rootObjects == null)
                return snapshots;

            foreach (var rootObject in rootObjects)
            {
                PilotObjectSnapshot snapshot;
                if (TryCopySnapshot(rootObject, null, out snapshot))
                    snapshots.Add(snapshot);
            }

            return snapshots
                .GroupBy(snapshot => snapshot.Id)
                .Select(group => group.First())
                .ToList();
        }

        private static bool TryCopySnapshot(
            PilotDataObject dataObject,
            IObjectsRepository objectsRepository,
            out PilotObjectSnapshot snapshot)
        {
            snapshot = null;

            if (dataObject == null)
                return false;

            try
            {
                var id = dataObject.Id;
                if (id == Guid.Empty)
                    return false;

                var files = dataObject.Files == null
                    ? new List<PilotFileSnapshot>()
                    : dataObject.Files
                        .Select(file => new PilotFileSnapshot(file.Id, file.Name, file.Size))
                        .ToList();

                snapshot = new PilotObjectSnapshot(
                    id,
                    dataObject.ParentId,
                    dataObject.DisplayName,
                    dataObject.Type == null ? "" : dataObject.Type.Title,
                    files,
                    GetChildIds(dataObject, objectsRepository));

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<Guid> GetChildIds(PilotDataObject dataObject, IObjectsRepository objectsRepository)
        {
            var rawChildren = new List<Guid>();

            try
            {
                rawChildren = dataObject.Children == null
                    ? new List<Guid>()
                    : dataObject.Children
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .ToList();
            }
            catch
            {
                rawChildren = new List<Guid>();
            }

            if (rawChildren.Count > 0 || objectsRepository == null)
                return rawChildren;

            if (objectsRepository != null)
            {
                try
                {
                    return ChildrenFilters.GetChildrenForPilotStorage(dataObject, objectsRepository)
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .ToList();
                }
                catch
                {
                    return rawChildren;
                }
            }

            return rawChildren;
        }

        private sealed class SdkTreeBuilder
        {
            private readonly IObjectsRepository _objectsRepository;
            private int _objectCount;
            private int _fileCount;
            private int _skippedObjectCount;

            public SdkTreeBuilder(IObjectsRepository objectsRepository)
            {
                _objectsRepository = objectsRepository;
            }

            public PilotSizeReport Build(
                IList<PilotObjectSnapshot> initialRootSnapshots,
                IProgress<ScanProgress> progress,
                CancellationToken token)
            {
                var seedSnapshots = initialRootSnapshots == null
                    ? new List<PilotObjectSnapshot>()
                    : initialRootSnapshots.Where(snapshot => snapshot != null).ToList();

                if (seedSnapshots.Count == 0)
                    seedSnapshots.Add(GetSdkRootSnapshot());

                PilotSizeNode root;
                var queue = new Queue<PilotSizeNode>();
                var visited = new HashSet<Guid>();

                if (seedSnapshots.Count == 1)
                {
                    root = PilotSizeNode.CreateObject(seedSnapshots[0], null);
                    queue.Enqueue(root);
                    visited.Add(root.Id);
                }
                else
                {
                    root = PilotSizeNode.CreateVirtualRoot("Выбранные объекты");
                    foreach (var snapshot in seedSnapshots.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var child = PilotSizeNode.CreateObject(snapshot, root);
                        root.Children.Add(child);
                        queue.Enqueue(child);
                        visited.Add(child.Id);
                    }
                }

                _objectCount = seedSnapshots.Count;
                _fileCount = seedSnapshots.Sum(snapshot => snapshot.Files.Count);

                ReportProgress(progress, root.DisplayName);

                while (queue.Count > 0)
                {
                    token.ThrowIfCancellationRequested();

                    var parent = queue.Dequeue();
                    var childIds = parent.PendingChildIds
                        .Where(id => id != Guid.Empty && !visited.Contains(id))
                        .Distinct()
                        .ToList();

                    foreach (var id in childIds)
                        visited.Add(id);

                    if (childIds.Count == 0)
                        continue;

                    foreach (var chunk in SplitByChunks(childIds, SubscribeChunkSize))
                    {
                        token.ThrowIfCancellationRequested();

                        var childSnapshots = LoadSnapshots(chunk, token);
                        foreach (var childSnapshot in childSnapshots.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                        {
                            token.ThrowIfCancellationRequested();

                            var childNode = PilotSizeNode.CreateObject(childSnapshot, parent);
                            parent.Children.Add(childNode);
                            queue.Enqueue(childNode);

                            _objectCount++;
                            _fileCount += childSnapshot.Files.Count;

                            if (_objectCount % 50 == 0)
                                ReportProgress(progress, childNode.DisplayName);
                        }
                    }
                }

                root.CalculateTotals();
                ReportProgress(progress, root.DisplayName);

                return new PilotSizeReport(root, _objectCount, _skippedObjectCount);
            }

            private List<PilotObjectSnapshot> LoadSnapshots(IList<Guid> ids, CancellationToken token)
            {
                var snapshots = new List<PilotObjectSnapshot>();
                var remaining = new HashSet<Guid>(ids);

                using (var completed = new ManualResetEventSlim(false))
                {
                    IDisposable subscription = null;
                    var observer = new DataObjectObserver(
                        dataObject =>
                        {
                            PilotObjectSnapshot snapshot;
                            if (!TryCopySnapshot(dataObject, out snapshot))
                                return;

                            lock (remaining)
                            {
                                if (!remaining.Remove(snapshot.Id))
                                    return;

                                snapshots.Add(snapshot);
                                if (remaining.Count == 0)
                                    completed.Set();
                            }
                        },
                        error => completed.Set(),
                        () => completed.Set());

                    try
                    {
                        subscription = _objectsRepository.SubscribeObjects(ids).Subscribe(observer);
                        completed.Wait(TimeSpan.FromSeconds(SubscribeTimeoutSeconds), token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // A partial tree is more useful than a failed report when one SDK request fails.
                    }
                    finally
                    {
                        if (subscription != null)
                            subscription.Dispose();
                    }
                }

                _skippedObjectCount += remaining.Count;
                return snapshots;
            }

            private PilotObjectSnapshot GetSdkRootSnapshot()
            {
                try
                {
#pragma warning disable 0612
                    var rootObject = _objectsRepository.GetRootObject();
#pragma warning restore 0612
                    return CopySnapshot(rootObject);
                }
                catch (NotSupportedException ex)
                {
                    throw new InvalidOperationException(
                        "В этом контексте Pilot SDK не поддерживает получение корневого объекта. " +
                        "Выберите проект, папку или объект в дереве документов Pilot и запустите команду " +
                        "«Размер файлов БД от выбранного объекта» из контекстного меню.",
                        ex);
                }
            }

            private PilotObjectSnapshot CopySnapshot(PilotDataObject dataObject)
            {
                PilotObjectSnapshot snapshot;
                if (!FilesSizeReportWindow.TryCopySnapshot(dataObject, _objectsRepository, out snapshot))
                    throw new InvalidOperationException("Не удалось получить объект Pilot.");

                return snapshot;
            }

            private bool TryCopySnapshot(PilotDataObject dataObject, out PilotObjectSnapshot snapshot)
            {
                return FilesSizeReportWindow.TryCopySnapshot(dataObject, _objectsRepository, out snapshot);
            }

            private void ReportProgress(IProgress<ScanProgress> progress, string currentName)
            {
                if (progress == null)
                    return;

                progress.Report(new ScanProgress(_objectCount, _fileCount, currentName));
            }

            private static IEnumerable<List<Guid>> SplitByChunks(IList<Guid> ids, int chunkSize)
            {
                for (var i = 0; i < ids.Count; i += chunkSize)
                    yield return ids.Skip(i).Take(chunkSize).ToList();
            }
        }

        private sealed class PilotSizeReport
        {
            public PilotSizeReport(PilotSizeNode root, int objectCount, int skippedObjectCount)
            {
                Root = root;
                ObjectCount = objectCount;
                SkippedObjectCount = skippedObjectCount;
            }

            public PilotSizeNode Root { get; private set; }
            public int ObjectCount { get; private set; }
            public int SkippedObjectCount { get; private set; }
        }

        private sealed class ScanProgress
        {
            public ScanProgress(int objectCount, int fileCount, string currentName)
            {
                ObjectCount = objectCount;
                FileCount = fileCount;
                CurrentName = currentName;
            }

            public int ObjectCount { get; private set; }
            public int FileCount { get; private set; }
            public string CurrentName { get; private set; }
        }

        private sealed class PilotObjectSnapshot
        {
            public PilotObjectSnapshot(
                Guid id,
                Guid parentId,
                string displayName,
                string typeTitle,
                List<PilotFileSnapshot> files,
                List<Guid> childIds)
            {
                Id = id;
                ParentId = parentId;
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? id.ToString() : displayName;
                TypeTitle = typeTitle;
                Files = files ?? new List<PilotFileSnapshot>();
                ChildIds = childIds ?? new List<Guid>();
            }

            public Guid Id { get; private set; }
            public Guid ParentId { get; private set; }
            public string DisplayName { get; private set; }
            public string TypeTitle { get; private set; }
            public List<PilotFileSnapshot> Files { get; private set; }
            public List<Guid> ChildIds { get; private set; }
        }

        private sealed class PilotFileSnapshot
        {
            public PilotFileSnapshot(Guid id, string name, long size)
            {
                Id = id;
                Name = string.IsNullOrWhiteSpace(name) ? id.ToString() : name;
                Size = size;
            }

            public Guid Id { get; private set; }
            public string Name { get; private set; }
            public long Size { get; private set; }
        }

        private sealed class PilotSizeNode
        {
            private PilotSizeNode(
                NodeKind kind,
                Guid id,
                string displayName,
                string typeTitle,
                long ownSizeBytes,
                PilotSizeNode parent)
            {
                Kind = kind;
                Id = id;
                DisplayName = displayName;
                TypeTitle = typeTitle;
                OwnSizeBytes = ownSizeBytes;
                Parent = parent;
                Children = new List<PilotSizeNode>();
                PendingChildIds = new List<Guid>();
                PathText = BuildPath(parent, displayName);
            }

            public NodeKind Kind { get; private set; }
            public Guid Id { get; private set; }
            public string DisplayName { get; private set; }
            public string TypeTitle { get; private set; }
            public string PathText { get; private set; }
            public PilotSizeNode Parent { get; private set; }
            public List<PilotSizeNode> Children { get; private set; }
            public List<Guid> PendingChildIds { get; private set; }
            public long OwnSizeBytes { get; private set; }
            public long TotalSizeBytes { get; private set; }
            public int TotalFileCount { get; private set; }
            public int TotalObjectCount { get; private set; }
            public bool HasChildren { get { return Children.Count > 0; } }

            public static PilotSizeNode CreateObject(PilotObjectSnapshot snapshot, PilotSizeNode parent)
            {
                var node = new PilotSizeNode(
                    NodeKind.Object,
                    snapshot.Id,
                    snapshot.DisplayName,
                    snapshot.TypeTitle,
                    snapshot.Files.Sum(file => file.Size),
                    parent);

                node.PendingChildIds.AddRange(snapshot.ChildIds);

                foreach (var file in snapshot.Files.OrderByDescending(file => file.Size).ThenBy(file => file.Name))
                    node.Children.Add(CreateFile(file, node));

                return node;
            }

            public static PilotSizeNode CreateVirtualRoot(string displayName)
            {
                return new PilotSizeNode(NodeKind.Virtual, Guid.Empty, displayName, "Виртуальный корень", 0, null);
            }

            private static PilotSizeNode CreateFile(PilotFileSnapshot file, PilotSizeNode parent)
            {
                return new PilotSizeNode(NodeKind.File, file.Id, file.Name, "Файл", file.Size, parent);
            }

            public void CalculateTotals()
            {
                if (Kind == NodeKind.File)
                {
                    TotalSizeBytes = OwnSizeBytes;
                    TotalFileCount = 1;
                    TotalObjectCount = 0;
                    return;
                }

                long size = 0;
                var files = 0;
                var objects = Kind == NodeKind.Virtual ? 0 : 1;

                foreach (var child in Children)
                {
                    child.CalculateTotals();
                    size += child.TotalSizeBytes;
                    files += child.TotalFileCount;
                    objects += child.TotalObjectCount;
                }

                TotalSizeBytes = size;
                TotalFileCount = files;
                TotalObjectCount = objects;
            }

            private static string BuildPath(PilotSizeNode parent, string displayName)
            {
                if (parent == null || string.IsNullOrEmpty(parent.PathText))
                    return displayName;

                return parent.PathText + "\\" + displayName;
            }
        }

        private enum NodeKind
        {
            Virtual,
            Object,
            File
        }

        private sealed class ChartBarViewModel
        {
            private const double MaxBarHeight = 282.0;

            public ChartBarViewModel(PilotSizeNode node, long maxSizeBytes)
            {
                Node = node;
                DisplayName = node.DisplayName;
                ShortName = TrimText(node.DisplayName, 30);
                PathText = node.PathText;
                TypeTitle = node.TypeTitle;
                KindText = node.Kind == NodeKind.File ? "Файл" : "Объект";
                SizeText = SizeFormatter.Format(node.TotalSizeBytes);
                OwnSizeText = node.Kind == NodeKind.File ? SizeText : SizeFormatter.Format(node.OwnSizeBytes);
                FileCount = node.TotalFileCount;
                ObjectCount = node.TotalObjectCount;
                if (node.Kind == NodeKind.File)
                {
                    BarBrush = CreateBarBrush(Color.FromRgb(91, 178, 132), Color.FromRgb(47, 137, 94));
                    BarBorderBrush = new SolidColorBrush(Color.FromRgb(36, 111, 74));
                }
                else
                {
                    BarBrush = CreateBarBrush(Color.FromRgb(76, 139, 214), Color.FromRgb(48, 103, 175));
                    BarBorderBrush = new SolidColorBrush(Color.FromRgb(39, 88, 151));
                }

                var ratio = maxSizeBytes <= 0 ? 0 : node.TotalSizeBytes / (double)maxSizeBytes;
                BarHeight = Math.Max(4.0, Math.Round(ratio * MaxBarHeight));

                TooltipText = DisplayName + Environment.NewLine +
                              "Тип: " + KindText + Environment.NewLine +
                              "Размер: " + SizeText + Environment.NewLine +
                              "Собственный размер: " + OwnSizeText + Environment.NewLine +
                              "Файлов: " + FileCount.ToString("N0", CultureInfo.InvariantCulture) + Environment.NewLine +
                              "Объектов: " + ObjectCount.ToString("N0", CultureInfo.InvariantCulture) + Environment.NewLine +
                              "Путь: " + PathText;
            }

            public PilotSizeNode Node { get; private set; }
            public string DisplayName { get; private set; }
            public string ShortName { get; private set; }
            public string PathText { get; private set; }
            public string TypeTitle { get; private set; }
            public string KindText { get; private set; }
            public string SizeText { get; private set; }
            public string OwnSizeText { get; private set; }
            public int FileCount { get; private set; }
            public int ObjectCount { get; private set; }
            public double BarHeight { get; private set; }
            public Brush BarBrush { get; private set; }
            public Brush BarBorderBrush { get; private set; }
            public string TooltipText { get; private set; }

            private static Brush CreateBarBrush(Color topColor, Color bottomColor)
            {
                var brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1)
                };
                brush.GradientStops.Add(new GradientStop(topColor, 0));
                brush.GradientStops.Add(new GradientStop(bottomColor, 1));
                brush.Freeze();
                return brush;
            }
        }

        private sealed class DataObjectObserver : IObserver<PilotDataObject>
        {
            private readonly Action<PilotDataObject> _onNext;
            private readonly Action<Exception> _onError;
            private readonly Action _onCompleted;

            public DataObjectObserver(Action<PilotDataObject> onNext, Action<Exception> onError, Action onCompleted)
            {
                _onNext = onNext;
                _onError = onError;
                _onCompleted = onCompleted;
            }

            public void OnCompleted()
            {
                _onCompleted();
            }

            public void OnError(Exception error)
            {
                _onError(error);
            }

            public void OnNext(PilotDataObject value)
            {
                _onNext(value);
            }
        }

        private static class SizeFormatter
        {
            private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

            public static string Format(long bytes)
            {
                var value = (double)bytes;
                var unitIndex = 0;

                while (value >= 1024.0 && unitIndex < Units.Length - 1)
                {
                    value /= 1024.0;
                    unitIndex++;
                }

                if (unitIndex == 0)
                    return bytes.ToString("N0", CultureInfo.InvariantCulture) + " " + Units[unitIndex];

                return value.ToString(value >= 10 ? "N1" : "N2", CultureInfo.InvariantCulture) + " " + Units[unitIndex];
            }
        }
    }
}
