using Ascon.Pilot.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        private const int StorageScanBatchSize = 200;
        private const int StorageMountWaitSeconds = 10;

        private readonly IObjectsRepository _objectsRepository;
        private readonly List<Guid> _initialRootIds;
        private readonly List<PilotObjectSnapshot> _initialRootSnapshots;

        private CancellationTokenSource _loadCancellation;
        private PilotSizeNode _rootNode;
        private PilotSizeNode _currentNode;
        private bool _loadStarted;

        public FilesSizeReportWindow(IObjectsRepository objectsRepository)
            : this(objectsRepository, null)
        {
        }

        public FilesSizeReportWindow(IObjectsRepository objectsRepository, IEnumerable<PilotDataObject> rootObjects)
        {
            _objectsRepository = objectsRepository;
            _initialRootIds = CopyInitialRootIds(rootObjects);
            _initialRootSnapshots = CopyInitialRootSnapshots(rootObjects, objectsRepository);

            InitializeComponent();
            Loaded += FilesSizeReportWindow_Loaded;
            Closed += FilesSizeReportWindow_Closed;

            if (_initialRootSnapshots.Count > 0)
            {
                EmptyChartTextBlock.Text = _initialRootSnapshots.Count == 1
                    ? "Подготовка сканирования выбранного объекта..."
                    : "Подготовка сканирования выбранных объектов...";
            }
        }

        private async void FilesSizeReportWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadStarted)
                return;

            _loadStarted = true;
            await LoadFromSdkAsync();
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

            ResetViewForLoading();

            try
            {
                var progress = new Progress<ScanProgress>(UpdateScanProgress);
                var report = await Task.Run(() =>
                {
                    var builder = new SdkTreeBuilder(_objectsRepository);
                    return builder.Build(_initialRootSnapshots, _initialRootIds, progress, token);
                }, token);

                token.ThrowIfCancellationRequested();

                _rootNode = report.Root;
                _currentNode = _rootNode;

                RootTextBlock.Text = _rootNode.DisplayName;
                SummaryTextBlock.Text = BuildSummaryText(report);

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
                _loadCancellation = null;
            }
        }

        private void ResetViewForLoading()
        {
            ProgressBar.IsIndeterminate = true;
            ProgressBar.Value = 0;
            RootTextBlock.Text = "";
            SummaryTextBlock.Text = "";
            StatusTextBlock.Text = _initialRootSnapshots.Count > 0 || _initialRootIds.Count > 0
                ? "Подготовка выбранных объектов..."
                : "Не выбран объект для сканирования.";
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

        private string BuildSummaryText(PilotSizeReport report)
        {
            var storageText = _rootNode.HasStorageMetric
                ? SizeFormatter.Format(_rootNode.StorageSizeBytes)
                : "недоступно";
            var mismatchText = _rootNode.HasStorageMetric
                ? SizeFormatter.FormatSigned(_rootNode.SizeMismatchBytes)
                : "недоступно";

            return string.Format(
                CultureInfo.InvariantCulture,
                "Объектов: {0:N0}; файлов SDK: {1:N0} (акт. {2:N0}, ист. {3:N0}); файлов Storage: {4:N0}; Pilot-Storage: {5}; SDK актуальный: {6}; SDK всего: {7}; разница: {8}; Storage unresolved: {9:N0}",
                report.ObjectCount,
                _rootNode.TotalFileCount,
                _rootNode.TotalActualFileCount,
                _rootNode.TotalHistoryFileCount,
                _rootNode.StorageFileCount,
                storageText,
                SizeFormatter.Format(_rootNode.TotalActualSizeBytes),
                SizeFormatter.Format(_rootNode.TotalSizeBytes),
                mismatchText,
                report.StorageUnresolvedCount);
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

            var children = GetDisplayChildren(_currentNode)
                .OrderByDescending(GetEffectiveSizeBytes)
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
                var maxSize = Math.Max(1, children.Max(GetEffectiveSizeBytes));
                var bars = children.Select(node => new ChartBarViewModel(node, maxSize)).ToList();
                ChartItemsControl.ItemsSource = bars;
                DetailsDataGrid.ItemsSource = bars;
                EmptyChartTextBlock.Visibility = Visibility.Collapsed;
                ChartScrollViewer.Visibility = Visibility.Visible;
            }

            RenderBreadcrumb();
        }

        private IEnumerable<PilotSizeNode> GetDisplayChildren(PilotSizeNode node)
        {
            if (UseStorageChildren(node))
                return node.StorageChildren;

            return node.Children;
        }

        private static bool UseStorageChildren(PilotSizeNode node)
        {
            return node != null &&
                   node.StorageChildren.Count > 0 &&
                   node.HasStorageMetric;
        }

        private bool HasDisplayChildren(PilotSizeNode node)
        {
            return node != null && GetDisplayChildren(node).Any();
        }

        private static long GetEffectiveSizeBytes(PilotSizeNode node)
        {
            if (node == null)
                return 0;

            return ShouldUseStorageMetric(node)
                ? node.StorageSizeBytes
                : node.IsPilotStorageObject
                    ? 0
                    : node.TotalActualSizeBytes;
        }

        private static string GetEffectiveSizeText(PilotSizeNode node)
        {
            if (node != null && node.IsPilotStorageObject && !ShouldUseStorageMetric(node))
                return "unresolved";

            return SizeFormatter.Format(GetEffectiveSizeBytes(node));
        }

        private static string GetEffectiveSizeBytesText(PilotSizeNode node)
        {
            if (node != null && node.IsPilotStorageObject && !ShouldUseStorageMetric(node))
                return "unresolved";

            return SizeFormatter.FormatBytes(GetEffectiveSizeBytes(node));
        }

        private static bool ShouldUseStorageMetric(PilotSizeNode node)
        {
            return node != null &&
                   node.HasStorageMetric &&
                   (!node.IsPilotStorageObject || node.HasResolvedStorageMetric || node.StorageChildren.Count > 0);
        }

        private static string GetStorageStatusText(PilotSizeNode node)
        {
            if (node == null)
                return "";

            if (node.HasResolvedStorageMetric)
                return string.IsNullOrWhiteSpace(node.StorageState) ? "resolved" : node.StorageState;

            if (node.IsPilotStorageObject)
                return "Storage unresolved";

            return node.HasStorageMetric ? "Storage aggregated" : "";
        }

        private static string GetEffectiveSizeSource(PilotSizeNode node)
        {
            if (ShouldUseStorageMetric(node))
                return "Pilot-Storage FileInfo.Length";

            return node != null && node.IsPilotStorageObject
                ? "Storage unresolved"
                : "Pilot SDK IFile.Size актуальный";
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
            if (bar == null || !HasDisplayChildren(bar.Node))
                return;

            _currentNode = bar.Node;
            RenderCurrentNode();
        }

        private void DetailsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var bar = DetailsDataGrid.SelectedItem as ChartBarViewModel;
            if (bar == null || !HasDisplayChildren(bar.Node))
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

        private static List<Guid> CopyInitialRootIds(IEnumerable<PilotDataObject> rootObjects)
        {
            var ids = new List<Guid>();
            if (rootObjects == null)
                return ids;

            foreach (var rootObject in rootObjects)
            {
                if (rootObject == null)
                    continue;

                try
                {
                    var id = rootObject.Id;
                    if (id != Guid.Empty)
                        ids.Add(id);
                }
                catch
                {
                    // A selected object with unreadable details will be handled by the snapshot fallback path.
                }
            }

            return ids
                .Distinct()
                .ToList();
        }

        private static List<PilotObjectSnapshot> CopyInitialRootSnapshots(
            IEnumerable<PilotDataObject> rootObjects,
            IObjectsRepository objectsRepository)
        {
            var snapshots = new List<PilotObjectSnapshot>();
            if (rootObjects == null)
                return snapshots;

            foreach (var rootObject in rootObjects)
            {
                PilotObjectSnapshot snapshot;
                if (TryCopySnapshot(rootObject, objectsRepository, out snapshot))
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

                Guid parentId;
                try
                {
                    parentId = dataObject.ParentId;
                }
                catch
                {
                    parentId = Guid.Empty;
                }

                string displayName;
                try
                {
                    displayName = dataObject.DisplayName;
                }
                catch
                {
                    displayName = "";
                }

                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = id.ToString();

                string typeTitle;
                string typeName;
                try
                {
                    typeTitle = dataObject.Type == null ? "" : dataObject.Type.Title;
                    typeName = dataObject.Type == null ? "" : dataObject.Type.Name;
                }
                catch
                {
                    typeTitle = "";
                    typeName = "";
                }

                var files = CopyFileSnapshots(dataObject);

                snapshot = new PilotObjectSnapshot(
                    id,
                    parentId,
                    displayName,
                    typeTitle,
                    typeName,
                    IsMountable(dataObject),
                    files,
                    GetChildIds(dataObject, objectsRepository),
                    GetSourceFileIds(dataObject));

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<PilotFileSnapshot> CopyFileSnapshots(PilotDataObject dataObject)
        {
            var filesById = new Dictionary<Guid, PilotFileSnapshot>();

            IFilesSnapshot actualSnapshot = null;
            try
            {
                actualSnapshot = dataObject.ActualFileSnapshot;
            }
            catch
            {
                actualSnapshot = null;
            }

            if (actualSnapshot != null)
                CopyFilesFromSnapshot(actualSnapshot, true, filesById);
            else
                CopyActualFilesFallback(dataObject, filesById);

            try
            {
                if (dataObject.PreviousFileSnapshots != null)
                {
                    foreach (var snapshot in dataObject.PreviousFileSnapshots)
                        CopyFilesFromSnapshot(snapshot, false, filesById);
                }
            }
            catch
            {
                // History is optional for this report; keep the actual files if history cannot be read.
            }

            return filesById.Values
                .OrderByDescending(file => file.IsActual)
                .ThenByDescending(file => file.Size)
                .ThenBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static void CopyActualFilesFallback(
            PilotDataObject dataObject,
            Dictionary<Guid, PilotFileSnapshot> filesById)
        {
            try
            {
                if (dataObject.Files == null)
                    return;

                foreach (var file in dataObject.Files)
                    CopyFile(file, true, DateTime.MinValue, "", filesById);
            }
            catch
            {
                // Leave the object without own files if even the actual files cannot be read.
            }
        }

        private static void CopyFilesFromSnapshot(
            IFilesSnapshot snapshot,
            bool isActual,
            Dictionary<Guid, PilotFileSnapshot> filesById)
        {
            if (snapshot == null)
                return;

            DateTime created;
            string reason;

            try
            {
                created = snapshot.Created;
            }
            catch
            {
                created = DateTime.MinValue;
            }

            try
            {
                reason = snapshot.Reason;
            }
            catch
            {
                reason = "";
            }

            try
            {
                if (snapshot.Files == null)
                    return;

                foreach (var file in snapshot.Files)
                    CopyFile(file, isActual, created, reason, filesById);
            }
            catch
            {
                // A single unreadable snapshot must not fail the entire object scan.
            }
        }

        private static void CopyFile(
            IFile file,
            bool isActual,
            DateTime snapshotCreated,
            string snapshotReason,
            Dictionary<Guid, PilotFileSnapshot> filesById)
        {
            if (file == null)
                return;

            Guid id;
            try
            {
                id = file.Id;
            }
            catch
            {
                return;
            }

            if (id == Guid.Empty)
                return;

            if (filesById.ContainsKey(id))
                return;

            string name;
            long size;

            try
            {
                name = file.Name;
            }
            catch
            {
                name = id.ToString();
            }

            try
            {
                size = Math.Max(0L, file.Size);
            }
            catch
            {
                size = 0;
            }

            filesById.Add(id, new PilotFileSnapshot(id, name, size, isActual, snapshotCreated, snapshotReason));
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

        private static List<Guid> GetSourceFileIds(PilotDataObject dataObject)
        {
            try
            {
                return dataObject.RelatedSourceFiles == null
                    ? new List<Guid>()
                    : dataObject.RelatedSourceFiles
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .ToList();
            }
            catch
            {
                return new List<Guid>();
            }
        }

        private static bool IsMountable(PilotDataObject dataObject)
        {
            try
            {
                return dataObject != null && dataObject.Type != null && dataObject.Type.IsMountable;
            }
            catch
            {
                return false;
            }
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
                IList<Guid> initialRootIds,
                IProgress<ScanProgress> progress,
                CancellationToken token)
            {
                var seedSnapshots = ResolveSeedSnapshots(initialRootSnapshots, initialRootIds, token);
                if (seedSnapshots.Count == 0)
                    throw new InvalidOperationException("Не удалось загрузить выбранный объект.");

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

                ApplySourceFileGrouping(root);
                ReportProgress(progress, "Pilot-Storage");
                var storageBuilder = new StorageTreeBuilder(_objectsRepository);
                storageBuilder.Apply(root, initialRootIds, token);
                root.CalculateTotals();
                root.CalculateStorageTotals();
                ReportProgress(progress, root.DisplayName);

                return new PilotSizeReport(root, _objectCount, _skippedObjectCount, CountStorageUnresolved(root));
            }

            private static int CountStorageUnresolved(PilotSizeNode root)
            {
                return root == null
                    ? 0
                    : root.EnumerateObjectNodes().Count(node => node.IsPilotStorageObject && !node.HasResolvedStorageMetric);
            }

            private List<PilotObjectSnapshot> ResolveSeedSnapshots(
                IList<PilotObjectSnapshot> initialRootSnapshots,
                IList<Guid> initialRootIds,
                CancellationToken token)
            {
                var seedById = new Dictionary<Guid, PilotObjectSnapshot>();
                if (initialRootSnapshots != null)
                {
                    foreach (var snapshot in initialRootSnapshots.Where(snapshot => snapshot != null && snapshot.Id != Guid.Empty))
                    {
                        if (!seedById.ContainsKey(snapshot.Id))
                            seedById.Add(snapshot.Id, snapshot);
                    }
                }

                var rootIds = initialRootIds == null
                    ? new List<Guid>()
                    : initialRootIds.Where(id => id != Guid.Empty).Distinct().ToList();

                foreach (var id in rootIds)
                {
                    token.ThrowIfCancellationRequested();

                    PilotObjectSnapshot cachedSnapshot;
                    if (!TryLoadCachedSnapshot(id, out cachedSnapshot))
                        continue;

                    PilotObjectSnapshot currentSnapshot;
                    if (!seedById.TryGetValue(id, out currentSnapshot))
                    {
                        seedById[id] = cachedSnapshot;
                    }
                    else if (IsBetterSnapshot(cachedSnapshot, currentSnapshot))
                    {
                        seedById[id] = cachedSnapshot;
                    }
                }

                var missingIds = rootIds
                    .Where(id => !seedById.ContainsKey(id) || IsBlankCachedSnapshot(seedById[id], id))
                    .ToList();

                if (missingIds.Count > 0)
                {
                    foreach (var snapshot in LoadSnapshots(missingIds, token))
                    {
                        if (snapshot == null || snapshot.Id == Guid.Empty)
                            continue;

                        PilotObjectSnapshot currentSnapshot;
                        if (!seedById.TryGetValue(snapshot.Id, out currentSnapshot) ||
                            IsBlankCachedSnapshot(currentSnapshot, snapshot.Id) ||
                            IsBetterSnapshot(snapshot, currentSnapshot))
                        {
                            seedById[snapshot.Id] = snapshot;
                        }
                    }
                }

                if (rootIds.Count > 0)
                    return rootIds
                        .Where(id => seedById.ContainsKey(id) && !IsBlankCachedSnapshot(seedById[id], id))
                        .Select(id => seedById[id])
                        .ToList();

                return seedById.Values
                    .OrderBy(snapshot => snapshot.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            private bool TryLoadCachedSnapshot(Guid id, out PilotObjectSnapshot snapshot)
            {
                snapshot = null;
                if (_objectsRepository == null || id == Guid.Empty)
                    return false;

                try
                {
#pragma warning disable 0612
                    var dataObject = _objectsRepository.GetCachedObject(id);
#pragma warning restore 0612
                    return TryCopySnapshot(dataObject, out snapshot) && !IsBlankCachedSnapshot(snapshot, id);
                }
                catch
                {
                    return false;
                }
            }

            private static bool IsBlankCachedSnapshot(PilotObjectSnapshot snapshot, Guid expectedId)
            {
                return snapshot == null ||
                       snapshot.Id == Guid.Empty ||
                       (snapshot.Id == expectedId &&
                        string.Equals(snapshot.DisplayName, expectedId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                        string.IsNullOrWhiteSpace(snapshot.TypeTitle) &&
                        snapshot.Files.Count == 0 &&
                        snapshot.ChildIds.Count == 0 &&
                        snapshot.SourceFileIds.Count == 0);
            }

            private static bool IsBetterSnapshot(PilotObjectSnapshot candidate, PilotObjectSnapshot current)
            {
                if (candidate == null)
                    return false;

                if (current == null)
                    return true;

                return GetSnapshotScore(candidate) > GetSnapshotScore(current);
            }

            private static int GetSnapshotScore(PilotObjectSnapshot snapshot)
            {
                if (snapshot == null)
                    return 0;

                var score = 0;
                score += snapshot.Files.Count * 2;
                score += snapshot.ChildIds.Count * 4;
                score += snapshot.SourceFileIds.Count;
                if (!string.IsNullOrWhiteSpace(snapshot.TypeTitle))
                    score += 2;
                if (!string.IsNullOrWhiteSpace(snapshot.TypeName))
                    score += 2;
                if (!string.Equals(snapshot.DisplayName, snapshot.Id.ToString(), StringComparison.OrdinalIgnoreCase))
                    score += 2;
                if (snapshot.IsMountable)
                    score += 1;

                return score;
            }

            private static void ApplySourceFileGrouping(PilotSizeNode root)
            {
                if (root == null)
                    return;

                var objectNodes = root.EnumerateObjectNodes().ToList();
                var objectsById = objectNodes
                    .Where(node => node.Id != Guid.Empty)
                    .GroupBy(node => node.Id)
                    .ToDictionary(group => group.Key, group => group.First());

                var ownersBySourceId = new Dictionary<Guid, List<PilotSizeNode>>();

                foreach (var owner in objectNodes)
                {
                    var sourceFileIds = owner.SourceFileIds
                        .Where(id => id != Guid.Empty && id != owner.Id)
                        .Distinct()
                        .ToList();

                    foreach (var sourceFileId in sourceFileIds)
                    {
                        PilotSizeNode sourceNode;
                        if (!objectsById.TryGetValue(sourceFileId, out sourceNode))
                        {
                            owner.RegisterExternalSourceFileLink();
                            continue;
                        }

                        List<PilotSizeNode> owners;
                        if (!ownersBySourceId.TryGetValue(sourceFileId, out owners))
                        {
                            owners = new List<PilotSizeNode>();
                            ownersBySourceId.Add(sourceFileId, owners);
                        }

                        owners.Add(owner);
                    }
                }

                foreach (var pair in ownersBySourceId)
                {
                    PilotSizeNode sourceNode;
                    if (!objectsById.TryGetValue(pair.Key, out sourceNode))
                        continue;

                    var owners = pair.Value
                        .Where(owner => owner != null && !ReferenceEquals(owner, sourceNode))
                        .Distinct()
                        .ToList();

                    if (owners.Count == 0)
                        continue;

                    if (owners.Count > 1)
                    {
                        sourceNode.MarkMultipleSourceOwners(owners.Count);
                        continue;
                    }

                    var ownerNode = owners[0];
                    if (sourceNode.IsAncestorOf(ownerNode))
                        continue;

                    sourceNode.MarkSourceOwner(ownerNode);
                    ownerNode.RegisterGroupedSourceChild();

                    if (!ReferenceEquals(sourceNode.Parent, ownerNode) && sourceNode.Parent != null)
                        sourceNode.ReparentTo(ownerNode);
                }
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

        private sealed class StorageTreeBuilder
        {
            private readonly IObjectsRepository _objectsRepository;
            private readonly Dictionary<Guid, string> _storagePathByObjectId;
            private Dictionary<Guid, PilotSizeNode> _objectNodesById;

            public StorageTreeBuilder(IObjectsRepository objectsRepository)
            {
                _objectsRepository = objectsRepository;
                _storagePathByObjectId = new Dictionary<Guid, string>();
                _objectNodesById = new Dictionary<Guid, PilotSizeNode>();
            }

            public void Apply(PilotSizeNode root, IList<Guid> initialRootIds, CancellationToken token)
            {
                if (root == null || _objectsRepository == null)
                    return;

                _objectNodesById = root.EnumerateObjectNodes()
                    .Where(node => node.Id != Guid.Empty)
                    .GroupBy(node => node.Id)
                    .ToDictionary(group => group.Key, group => group.First());

                if (_objectNodesById.Count == 0)
                    return;

                var selectedRoots = GetSelectedRoots(root, initialRootIds);
                var storageRoots = FindDirectStorageRoots(_objectNodesById.Values, token);

                if (storageRoots.Count == 0)
                    storageRoots = FindGlobalStorageRoots(token);

                if (storageRoots.Count == 0)
                {
                    MountNearestCandidates(selectedRoots, token);
                    storageRoots = FindDirectStorageRoots(_objectNodesById.Values, token);
                }

                foreach (var storageRoot in SelectMinimalStorageRoots(storageRoots))
                {
                    token.ThrowIfCancellationRequested();
                    ScanStorageRoot(storageRoot.Node, storageRoot.Path, token);
                }
            }

            private List<PilotSizeNode> GetSelectedRoots(PilotSizeNode root, IList<Guid> initialRootIds)
            {
                var result = new List<PilotSizeNode>();
                var rootIds = initialRootIds == null
                    ? new List<Guid>()
                    : initialRootIds.Where(id => id != Guid.Empty).Distinct().ToList();

                foreach (var id in rootIds)
                {
                    PilotSizeNode node;
                    if (_objectNodesById.TryGetValue(id, out node))
                        result.Add(node);
                }

                if (result.Count > 0)
                    return result;

                return root.Kind == NodeKind.Virtual
                    ? root.Children.Where(child => child.Kind == NodeKind.Object).ToList()
                    : new List<PilotSizeNode> { root };
            }

            private List<StorageRootCandidate> FindDirectStorageRoots(IEnumerable<PilotSizeNode> nodes, CancellationToken token)
            {
                var candidates = new List<StorageRootCandidate>();
                foreach (var node in nodes.Where(node => node != null && node.Id != Guid.Empty))
                {
                    token.ThrowIfCancellationRequested();

                    var storagePath = TryGetStoragePath(node.Id);
                    if (PathExists(storagePath))
                        candidates.Add(new StorageRootCandidate(node, storagePath));
                }

                return SelectMinimalStorageRoots(candidates);
            }

            private List<StorageRootCandidate> FindGlobalStorageRoots(CancellationToken token)
            {
                var result = new List<StorageRootCandidate>();
                var globalRoot = TryGetGlobalStoragePath();
                if (!Directory.Exists(globalRoot))
                    return result;

                List<string> entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(globalRoot).ToList();
                    entries.Add(globalRoot);
                }
                catch
                {
                    return result;
                }

                var storageObjects = GetStorageObjectsMap(entries);
                foreach (var storageObject in storageObjects.Values)
                {
                    token.ThrowIfCancellationRequested();

                    var id = GetStorageObjectId(storageObject);
                    if (id == Guid.Empty)
                        continue;

                    PilotSizeNode node;
                    if (!_objectNodesById.TryGetValue(id, out node))
                        continue;

                    var path = GetStorageObjectPath(storageObject);
                    if (PathExists(path))
                        result.Add(new StorageRootCandidate(node, path));
                }

                return SelectMinimalStorageRoots(result);
            }

            private void MountNearestCandidates(IEnumerable<PilotSizeNode> roots, CancellationToken token)
            {
                var mounted = new HashSet<Guid>();
                foreach (var candidate in GetNearestMountableCandidates(roots))
                {
                    token.ThrowIfCancellationRequested();

                    if (candidate == null || candidate.Id == Guid.Empty || !mounted.Add(candidate.Id))
                        continue;

                    if (PathExists(TryGetStoragePath(candidate.Id)))
                        continue;

                    try
                    {
                        _objectsRepository.MountAsync(candidate.Id).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        continue;
                    }

                    WaitForStoragePath(candidate.Id, token);
                }
            }

            private IEnumerable<PilotSizeNode> GetNearestMountableCandidates(IEnumerable<PilotSizeNode> roots)
            {
                if (roots == null)
                    yield break;

                foreach (var root in roots.Where(root => root != null))
                {
                    if (root.IsMountable)
                    {
                        yield return root;
                        continue;
                    }

                    foreach (var candidate in GetNearestMountableDescendants(root))
                        yield return candidate;
                }
            }

            private IEnumerable<PilotSizeNode> GetNearestMountableDescendants(PilotSizeNode node)
            {
                foreach (var child in node.Children.Where(child => child.Kind == NodeKind.Object))
                {
                    if (child.IsMountable)
                    {
                        yield return child;
                        continue;
                    }

                    foreach (var descendant in GetNearestMountableDescendants(child))
                        yield return descendant;
                }
            }

            private void WaitForStoragePath(Guid objectId, CancellationToken token)
            {
                var deadline = DateTime.UtcNow.AddSeconds(StorageMountWaitSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    _storagePathByObjectId.Remove(objectId);

                    if (PathExists(TryGetStoragePath(objectId)))
                        return;

                    Thread.Sleep(250);
                }
            }

            private List<StorageRootCandidate> SelectMinimalStorageRoots(IEnumerable<StorageRootCandidate> candidates)
            {
                var result = new List<StorageRootCandidate>();
                if (candidates == null)
                    return result;

                foreach (var candidate in candidates
                    .Where(candidate => candidate != null && candidate.Node != null && PathExists(candidate.Path))
                    .OrderBy(candidate => GetDepth(candidate.Node))
                    .ThenBy(candidate => candidate.Path.Length))
                {
                    if (result.Any(selected =>
                        selected.Node.IsAncestorOf(candidate.Node) ||
                        IsNestedPath(selected.Path, candidate.Path)))
                    {
                        continue;
                    }

                    result.Add(candidate);
                }

                return result;
            }

            private void ScanStorageRoot(PilotSizeNode node, string storagePath, CancellationToken token)
            {
                if (node == null || string.IsNullOrWhiteSpace(storagePath))
                    return;

                var rootStorageObject = GetStorageObjectsMap(new[] { storagePath })
                    .Values
                    .FirstOrDefault();

                ApplyStorageInfo(node, storagePath, rootStorageObject);

                if (Directory.Exists(storagePath))
                    AddDirectoryChildren(node, storagePath, token);
            }

            private void AddDirectoryChildren(PilotSizeNode parent, string directoryPath, CancellationToken token)
            {
                List<string> entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(directoryPath).ToList();
                }
                catch
                {
                    return;
                }

                var storageObjects = GetStorageObjectsMap(entries);
                foreach (var entry in entries.OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();

                    IStorageDataObject storageObject;
                    storageObjects.TryGetValue(NormalizePath(entry), out storageObject);

                    if (Directory.Exists(entry))
                    {
                        var child = GetOrCreateStorageDirectory(parent, storageObject, entry);
                        if (!ReferenceEquals(parent, child))
                            parent.AddStorageChild(child);

                        AddDirectoryChildren(child, entry, token);
                    }
                    else if (File.Exists(entry))
                    {
                        var child = GetOrCreateStorageFile(parent, storageObject, entry);
                        if (!ReferenceEquals(parent, child))
                            parent.AddStorageChild(child);
                    }
                }
            }

            private PilotSizeNode GetOrCreateStorageDirectory(PilotSizeNode parent, IStorageDataObject storageObject, string path)
            {
                PilotSizeNode node;
                var id = GetStorageObjectId(storageObject);
                if (id != Guid.Empty && _objectNodesById.TryGetValue(id, out node))
                {
                    ApplyStorageInfo(node, path, storageObject);
                    return node;
                }

                node = PilotSizeNode.CreateStorageDirectory(
                    id,
                    GetDisplayName(path),
                    GetStorageTypeTitle(storageObject),
                    GetStorageTypeName(storageObject),
                    path,
                    GetStorageState(storageObject),
                    parent);

                return node;
            }

            private PilotSizeNode GetOrCreateStorageFile(PilotSizeNode parent, IStorageDataObject storageObject, string path)
            {
                PilotSizeNode node;
                var id = GetStorageObjectId(storageObject);
                if (id != Guid.Empty && _objectNodesById.TryGetValue(id, out node))
                {
                    ApplyStorageInfo(node, path, storageObject);
                    return node;
                }

                node = PilotSizeNode.CreateStorageFile(
                    id,
                    GetDisplayName(path),
                    GetStorageTypeTitle(storageObject),
                    GetStorageTypeName(storageObject),
                    path,
                    GetStorageState(storageObject),
                    GetFileLength(path),
                    parent);

                return node;
            }

            private void ApplyStorageInfo(PilotSizeNode node, string path, IStorageDataObject storageObject)
            {
                if (node == null || string.IsNullOrWhiteSpace(path))
                    return;

                var isFile = File.Exists(path);
                node.SetStorageInfo(
                    path,
                    GetStorageState(storageObject),
                    isFile ? GetFileLength(path) : 0,
                    isFile ? 1 : 0,
                    isFile || Directory.Exists(path));
            }

            private string TryGetStoragePath(Guid objectId)
            {
                string cachedPath;
                if (_storagePathByObjectId.TryGetValue(objectId, out cachedPath))
                    return cachedPath;

                try
                {
                    cachedPath = _objectsRepository.GetStoragePath(objectId) ?? "";
                }
                catch
                {
                    cachedPath = "";
                }

                _storagePathByObjectId[objectId] = cachedPath;
                return cachedPath;
            }

            private string TryGetGlobalStoragePath()
            {
                try
                {
                    return _objectsRepository.GetStoragePath() ?? "";
                }
                catch
                {
                    return "";
                }
            }

            private Dictionary<string, IStorageDataObject> GetStorageObjectsMap(IEnumerable<string> paths)
            {
                var map = new Dictionary<string, IStorageDataObject>(StringComparer.OrdinalIgnoreCase);
                var pathList = paths == null
                    ? new List<string>()
                    : paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                for (var i = 0; i < pathList.Count; i += StorageScanBatchSize)
                {
                    var chunk = pathList.Skip(i).Take(StorageScanBatchSize).ToList();
                    try
                    {
                        foreach (var storageObject in _objectsRepository.GetStorageObjects(chunk))
                        {
                            var path = GetStorageObjectPath(storageObject);
                            if (string.IsNullOrWhiteSpace(path))
                                continue;

                            map[NormalizePath(path)] = storageObject;
                        }
                    }
                    catch
                    {
                        // Missing storage metadata must not prevent file-system size counting.
                    }
                }

                return map;
            }

            private static bool PathExists(string path)
            {
                return !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));
            }

            private static bool IsNestedPath(string parentPath, string childPath)
            {
                var parent = NormalizePath(parentPath);
                var child = NormalizePath(childPath);

                if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child))
                    return false;

                if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase))
                    return true;

                parent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
            }

            private static string NormalizePath(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return "";

                try
                {
                    return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }

            private static int GetDepth(PilotSizeNode node)
            {
                var depth = 0;
                var current = node == null ? null : node.Parent;
                while (current != null)
                {
                    depth++;
                    current = current.Parent;
                }

                return depth;
            }

            private static string GetDisplayName(string path)
            {
                var name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? path : name;
            }

            private static long GetFileLength(string path)
            {
                try
                {
                    return Math.Max(0L, new FileInfo(path).Length);
                }
                catch
                {
                    return 0;
                }
            }

            private static Guid GetStorageObjectId(IStorageDataObject storageObject)
            {
                try
                {
                    return storageObject == null || storageObject.DataObject == null
                        ? Guid.Empty
                        : storageObject.DataObject.Id;
                }
                catch
                {
                    return Guid.Empty;
                }
            }

            private static string GetStorageObjectPath(IStorageDataObject storageObject)
            {
                try
                {
                    return storageObject == null ? "" : storageObject.Path;
                }
                catch
                {
                    return "";
                }
            }

            private static string GetStorageTypeTitle(IStorageDataObject storageObject)
            {
                try
                {
                    return storageObject == null || storageObject.DataObject == null || storageObject.DataObject.Type == null
                        ? ""
                        : storageObject.DataObject.Type.Title;
                }
                catch
                {
                    return "";
                }
            }

            private static string GetStorageTypeName(IStorageDataObject storageObject)
            {
                try
                {
                    return storageObject == null || storageObject.DataObject == null || storageObject.DataObject.Type == null
                        ? ""
                        : storageObject.DataObject.Type.Name;
                }
                catch
                {
                    return "";
                }
            }

            private static string GetStorageState(IStorageDataObject storageObject)
            {
                try
                {
                    return storageObject == null ? "" : storageObject.State.ToString();
                }
                catch
                {
                    return "";
                }
            }

            private sealed class StorageRootCandidate
            {
                public StorageRootCandidate(PilotSizeNode node, string path)
                {
                    Node = node;
                    Path = path ?? "";
                }

                public PilotSizeNode Node { get; private set; }
                public string Path { get; private set; }
            }
        }

        private sealed class PilotSizeReport
        {
            public PilotSizeReport(PilotSizeNode root, int objectCount, int skippedObjectCount, int storageUnresolvedCount)
            {
                Root = root;
                ObjectCount = objectCount;
                SkippedObjectCount = skippedObjectCount;
                StorageUnresolvedCount = storageUnresolvedCount;
            }

            public PilotSizeNode Root { get; private set; }
            public int ObjectCount { get; private set; }
            public int SkippedObjectCount { get; private set; }
            public int StorageUnresolvedCount { get; private set; }
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
                string typeName,
                bool isMountable,
                List<PilotFileSnapshot> files,
                List<Guid> childIds,
                List<Guid> sourceFileIds)
            {
                Id = id;
                ParentId = parentId;
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? id.ToString() : displayName;
                TypeTitle = typeTitle;
                TypeName = typeName ?? "";
                IsMountable = isMountable;
                IsPilotStorageFile = string.Equals(TypeName, SystemTypeNames.PROJECT_FILE, StringComparison.OrdinalIgnoreCase);
                IsPilotStorageFolder = string.Equals(TypeName, SystemTypeNames.PROJECT_FOLDER, StringComparison.OrdinalIgnoreCase);
                Files = files ?? new List<PilotFileSnapshot>();
                ChildIds = childIds ?? new List<Guid>();
                SourceFileIds = sourceFileIds ?? new List<Guid>();
            }

            public Guid Id { get; private set; }
            public Guid ParentId { get; private set; }
            public string DisplayName { get; private set; }
            public string TypeTitle { get; private set; }
            public string TypeName { get; private set; }
            public bool IsMountable { get; private set; }
            public bool IsPilotStorageFile { get; private set; }
            public bool IsPilotStorageFolder { get; private set; }
            public List<PilotFileSnapshot> Files { get; private set; }
            public List<Guid> ChildIds { get; private set; }
            public List<Guid> SourceFileIds { get; private set; }
        }

        private sealed class PilotFileSnapshot
        {
            public PilotFileSnapshot(
                Guid id,
                string name,
                long size,
                bool isActual,
                DateTime snapshotCreated,
                string snapshotReason)
            {
                Id = id;
                Name = string.IsNullOrWhiteSpace(name) ? id.ToString() : name;
                Size = size;
                IsActual = isActual;
                SnapshotCreated = snapshotCreated;
                SnapshotReason = snapshotReason ?? "";
            }

            public Guid Id { get; private set; }
            public string Name { get; private set; }
            public long Size { get; private set; }
            public bool IsActual { get; private set; }
            public DateTime SnapshotCreated { get; private set; }
            public string SnapshotReason { get; private set; }
        }

        private sealed class PilotSizeNode
        {
            private PilotSizeNode(
                NodeKind kind,
                Guid id,
                string displayName,
                string typeTitle,
                string typeName,
                long ownSizeBytes,
                long ownActualSizeBytes,
                long ownHistorySizeBytes,
                int ownActualFileCount,
                int ownHistoryFileCount,
                bool isActualFile,
                DateTime fileSnapshotCreated,
                string fileSnapshotReason,
                bool isMountable,
                PilotSizeNode parent)
            {
                Kind = kind;
                Id = id;
                DisplayName = displayName;
                TypeTitle = typeTitle;
                TypeName = typeName ?? "";
                OwnSizeBytes = ownSizeBytes;
                OwnActualSizeBytes = ownActualSizeBytes;
                OwnHistorySizeBytes = ownHistorySizeBytes;
                OwnActualFileCount = ownActualFileCount;
                OwnHistoryFileCount = ownHistoryFileCount;
                IsActualFile = isActualFile;
                FileSnapshotCreated = fileSnapshotCreated;
                FileSnapshotReason = fileSnapshotReason ?? "";
                IsMountable = isMountable;
                Parent = parent;
                Children = new List<PilotSizeNode>();
                StorageChildren = new List<PilotSizeNode>();
                PendingChildIds = new List<Guid>();
                SourceFileIds = new List<Guid>();
                PathText = BuildPath(parent, displayName);
            }

            public NodeKind Kind { get; private set; }
            public Guid Id { get; private set; }
            public string DisplayName { get; private set; }
            public string TypeTitle { get; private set; }
            public string TypeName { get; private set; }
            public string PathText { get; private set; }
            public PilotSizeNode Parent { get; private set; }
            public List<PilotSizeNode> Children { get; private set; }
            public List<PilotSizeNode> StorageChildren { get; private set; }
            public List<Guid> PendingChildIds { get; private set; }
            public List<Guid> SourceFileIds { get; private set; }
            public long OwnSizeBytes { get; private set; }
            public long OwnActualSizeBytes { get; private set; }
            public long OwnHistorySizeBytes { get; private set; }
            public int OwnActualFileCount { get; private set; }
            public int OwnHistoryFileCount { get; private set; }
            public bool IsActualFile { get; private set; }
            public bool IsMountable { get; private set; }
            public DateTime FileSnapshotCreated { get; private set; }
            public string FileSnapshotReason { get; private set; }
            public long TotalSizeBytes { get; private set; }
            public long TotalActualSizeBytes { get; private set; }
            public long TotalHistorySizeBytes { get; private set; }
            public long OwnStorageSizeBytes { get; private set; }
            public long StorageSizeBytes { get; private set; }
            public int OwnStorageFileCount { get; private set; }
            public int StorageFileCount { get; private set; }
            public bool HasStorageMetric { get; private set; }
            public string StoragePath { get; private set; }
            public string StorageState { get; private set; }
            public long SizeMismatchBytes { get { return TotalActualSizeBytes - StorageSizeBytes; } }
            public bool IsPilotStorageFile
            {
                get
                {
                    return Kind == NodeKind.StorageFile ||
                           string.Equals(TypeName, SystemTypeNames.PROJECT_FILE, StringComparison.OrdinalIgnoreCase);
                }
            }
            public bool IsPilotStorageFolder
            {
                get
                {
                    return Kind == NodeKind.StorageDirectory ||
                           string.Equals(TypeName, SystemTypeNames.PROJECT_FOLDER, StringComparison.OrdinalIgnoreCase);
                }
            }
            public bool IsPilotStorageObject { get { return IsPilotStorageFile || IsPilotStorageFolder; } }
            public bool HasResolvedStorageMetric
            {
                get { return HasStorageMetric && !string.IsNullOrWhiteSpace(StoragePath); }
            }
            public int TotalFileCount { get; private set; }
            public int TotalActualFileCount { get; private set; }
            public int TotalHistoryFileCount { get; private set; }
            public int TotalObjectCount { get; private set; }
            public int ExternalSourceFileLinkCount { get; private set; }
            public int GroupedSourceChildCount { get; private set; }
            public int MultipleSourceOwnerCount { get; private set; }
            public string SourceOwnerDisplayName { get; private set; }
            public bool HasChildren { get { return Children.Count > 0 || StorageChildren.Count > 0; } }
            public string RelationText
            {
                get
                {
                    if (MultipleSourceOwnerCount > 0)
                        return "несколько SourceFiles-связей: " + MultipleSourceOwnerCount.ToString("N0", CultureInfo.InvariantCulture);

                    if (!string.IsNullOrWhiteSpace(SourceOwnerDisplayName))
                        return "часть модели: " + SourceOwnerDisplayName;

                    var parts = new List<string>();
                    if (GroupedSourceChildCount > 0)
                        parts.Add("частей модели: " + GroupedSourceChildCount.ToString("N0", CultureInfo.InvariantCulture));
                    if (ExternalSourceFileLinkCount > 0)
                        parts.Add("внешних SourceFiles: " + ExternalSourceFileLinkCount.ToString("N0", CultureInfo.InvariantCulture));

                    return string.Join("; ", parts);
                }
            }

            public static PilotSizeNode CreateObject(PilotObjectSnapshot snapshot, PilotSizeNode parent)
            {
                var actualFiles = snapshot.Files.Where(file => file.IsActual).ToList();
                var historyFiles = snapshot.Files.Where(file => !file.IsActual).ToList();
                var ownActualSizeBytes = actualFiles.Sum(file => file.Size);
                var ownHistorySizeBytes = historyFiles.Sum(file => file.Size);

                var node = new PilotSizeNode(
                    NodeKind.Object,
                    snapshot.Id,
                    snapshot.DisplayName,
                    snapshot.TypeTitle,
                    snapshot.TypeName,
                    ownActualSizeBytes + ownHistorySizeBytes,
                    ownActualSizeBytes,
                    ownHistorySizeBytes,
                    actualFiles.Count,
                    historyFiles.Count,
                    false,
                    DateTime.MinValue,
                    "",
                    snapshot.IsMountable,
                    parent);

                node.PendingChildIds.AddRange(snapshot.ChildIds);
                node.SourceFileIds.AddRange(snapshot.SourceFileIds);

                foreach (var file in snapshot.Files.OrderByDescending(file => file.Size).ThenBy(file => file.Name))
                    node.Children.Add(CreateFile(file, node));

                return node;
            }

            public static PilotSizeNode CreateVirtualRoot(string displayName)
            {
                return new PilotSizeNode(NodeKind.Virtual, Guid.Empty, displayName, "Виртуальный корень", "", 0, 0, 0, 0, 0, false, DateTime.MinValue, "", false, null);
            }

            private static PilotSizeNode CreateFile(PilotFileSnapshot file, PilotSizeNode parent)
            {
                return new PilotSizeNode(
                    NodeKind.File,
                    file.Id,
                    file.Name,
                    file.IsActual ? "Файл" : "Файл (история)",
                    "",
                    file.Size,
                    file.IsActual ? file.Size : 0,
                    file.IsActual ? 0 : file.Size,
                    file.IsActual ? 1 : 0,
                    file.IsActual ? 0 : 1,
                    file.IsActual,
                    file.SnapshotCreated,
                    file.SnapshotReason,
                    false,
                    parent);
            }

            public static PilotSizeNode CreateStorageDirectory(
                Guid id,
                string displayName,
                string typeTitle,
                string typeName,
                string storagePath,
                string storageState,
                PilotSizeNode parent)
            {
                var node = new PilotSizeNode(
                    NodeKind.StorageDirectory,
                    id,
                    displayName,
                    string.IsNullOrWhiteSpace(typeTitle) ? "Pilot-Storage папка" : typeTitle,
                    string.IsNullOrWhiteSpace(typeName) ? SystemTypeNames.PROJECT_FOLDER : typeName,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    DateTime.MinValue,
                    "",
                    false,
                    parent);

                node.SetStorageInfo(storagePath, storageState, 0, 0, true);
                return node;
            }

            public static PilotSizeNode CreateStorageFile(
                Guid id,
                string displayName,
                string typeTitle,
                string typeName,
                string storagePath,
                string storageState,
                long sizeBytes,
                PilotSizeNode parent)
            {
                var node = new PilotSizeNode(
                    NodeKind.StorageFile,
                    id,
                    displayName,
                    string.IsNullOrWhiteSpace(typeTitle) ? "Pilot-Storage файл" : typeTitle,
                    string.IsNullOrWhiteSpace(typeName) ? SystemTypeNames.PROJECT_FILE : typeName,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    DateTime.MinValue,
                    "",
                    false,
                    parent);

                node.SetStorageInfo(storagePath, storageState, Math.Max(0L, sizeBytes), 1, true);
                return node;
            }

            public void CalculateTotals()
            {
                if (Kind == NodeKind.File)
                {
                    TotalSizeBytes = OwnSizeBytes;
                    TotalActualSizeBytes = OwnActualSizeBytes;
                    TotalHistorySizeBytes = OwnHistorySizeBytes;
                    TotalFileCount = 1;
                    TotalActualFileCount = OwnActualFileCount;
                    TotalHistoryFileCount = OwnHistoryFileCount;
                    TotalObjectCount = 0;
                    return;
                }

                long size = 0;
                long actualSize = 0;
                long historySize = 0;
                var files = 0;
                var actualFiles = 0;
                var historyFiles = 0;
                var objects = Kind == NodeKind.Virtual ? 0 : 1;

                foreach (var child in Children)
                {
                    child.CalculateTotals();
                    size += child.TotalSizeBytes;
                    actualSize += child.TotalActualSizeBytes;
                    historySize += child.TotalHistorySizeBytes;
                    files += child.TotalFileCount;
                    actualFiles += child.TotalActualFileCount;
                    historyFiles += child.TotalHistoryFileCount;
                    objects += child.TotalObjectCount;
                }

                TotalSizeBytes = size;
                TotalActualSizeBytes = actualSize;
                TotalHistorySizeBytes = historySize;
                TotalFileCount = files;
                TotalActualFileCount = actualFiles;
                TotalHistoryFileCount = historyFiles;
                TotalObjectCount = objects;
            }

            public void SetStorageInfo(
                string storagePath,
                string storageState,
                long ownStorageSizeBytes,
                int ownStorageFileCount,
                bool hasStorageMetric)
            {
                StoragePath = storagePath ?? "";
                StorageState = storageState ?? "";
                OwnStorageSizeBytes = Math.Max(0L, ownStorageSizeBytes);
                OwnStorageFileCount = Math.Max(0, ownStorageFileCount);
                HasStorageMetric = hasStorageMetric;
            }

            public void AddStorageChild(PilotSizeNode child)
            {
                if (child != null && !StorageChildren.Contains(child))
                    StorageChildren.Add(child);
            }

            public void CalculateStorageTotals()
            {
                foreach (var child in Children)
                    child.CalculateStorageTotals();

                foreach (var child in StorageChildren)
                    child.CalculateStorageTotals();

                var storageChildrenSize = StorageChildren.Sum(child => child.StorageSizeBytes);
                var storageChildrenFiles = StorageChildren.Sum(child => child.StorageFileCount);
                var sdkChildrenSize = Children.Sum(child => child.StorageSizeBytes);
                var sdkChildrenFiles = Children.Sum(child => child.StorageFileCount);

                if (StorageChildren.Count > 0 || Kind == NodeKind.StorageFile || Kind == NodeKind.StorageDirectory)
                {
                    StorageSizeBytes = OwnStorageSizeBytes + storageChildrenSize;
                    StorageFileCount = OwnStorageFileCount + storageChildrenFiles;
                    HasStorageMetric = HasStorageMetric || StorageChildren.Count > 0 || OwnStorageFileCount > 0;
                    return;
                }

                StorageSizeBytes = OwnStorageSizeBytes + sdkChildrenSize;
                StorageFileCount = OwnStorageFileCount + sdkChildrenFiles;
                HasStorageMetric = HasStorageMetric || Children.Any(child => child.HasStorageMetric) || OwnStorageFileCount > 0;
            }

            public IEnumerable<PilotSizeNode> EnumerateObjectNodes()
            {
                if (Kind == NodeKind.Object)
                    yield return this;

                foreach (var child in Children)
                {
                    foreach (var node in child.EnumerateObjectNodes())
                        yield return node;
                }
            }

            public bool IsAncestorOf(PilotSizeNode node)
            {
                var current = node == null ? null : node.Parent;
                while (current != null)
                {
                    if (ReferenceEquals(current, this))
                        return true;

                    current = current.Parent;
                }

                return false;
            }

            public void ReparentTo(PilotSizeNode newParent)
            {
                if (newParent == null || Parent == null || ReferenceEquals(newParent, this))
                    return;

                Parent.Children.Remove(this);
                Parent = newParent;
                newParent.Children.Add(this);
                RefreshPathRecursive();
            }

            public void MarkSourceOwner(PilotSizeNode owner)
            {
                SourceOwnerDisplayName = owner == null ? "" : owner.DisplayName;
            }

            public void MarkMultipleSourceOwners(int ownerCount)
            {
                MultipleSourceOwnerCount = ownerCount;
            }

            public void RegisterGroupedSourceChild()
            {
                GroupedSourceChildCount++;
            }

            public void RegisterExternalSourceFileLink()
            {
                ExternalSourceFileLinkCount++;
            }

            private void RefreshPathRecursive()
            {
                PathText = BuildPath(Parent, DisplayName);
                foreach (var child in Children)
                    child.RefreshPathRecursive();
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
            File,
            StorageDirectory,
            StorageFile
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
                KindText = GetKindText(node);
                PrimarySizeText = FilesSizeReportWindow.GetEffectiveSizeText(node);
                PrimarySizeBytesText = FilesSizeReportWindow.GetEffectiveSizeBytesText(node);
                PrimarySourceText = FilesSizeReportWindow.GetEffectiveSizeSource(node);
                SizeText = SizeFormatter.Format(node.TotalSizeBytes);
                SizeBytesText = SizeFormatter.FormatBytes(node.TotalSizeBytes);
                ActualSizeText = SizeFormatter.Format(node.TotalActualSizeBytes);
                ActualSizeBytesText = SizeFormatter.FormatBytes(node.TotalActualSizeBytes);
                HistorySizeText = SizeFormatter.Format(node.TotalHistorySizeBytes);
                HistorySizeBytesText = SizeFormatter.FormatBytes(node.TotalHistorySizeBytes);
                StorageSizeText = node.HasStorageMetric ? SizeFormatter.Format(node.StorageSizeBytes) : "недоступно";
                StorageSizeBytesText = node.HasStorageMetric ? SizeFormatter.FormatBytes(node.StorageSizeBytes) : "недоступно";
                var hasComparableMetrics = FilesSizeReportWindow.ShouldUseStorageMetric(node) &&
                                           node.Kind != NodeKind.StorageFile &&
                                           node.Kind != NodeKind.StorageDirectory;
                MismatchText = hasComparableMetrics ? SizeFormatter.FormatSigned(node.SizeMismatchBytes) : "недоступно";
                MismatchBytesText = hasComparableMetrics ? SizeFormatter.FormatSignedBytes(node.SizeMismatchBytes) : "недоступно";
                StorageStateText = FilesSizeReportWindow.GetStorageStatusText(node);
                StoragePathText = string.IsNullOrWhiteSpace(node.StoragePath) ? "" : node.StoragePath;
                OwnSizeText = node.Kind == NodeKind.File ? SizeText : SizeFormatter.Format(node.OwnSizeBytes);
                OwnSizeBytesText = SizeFormatter.FormatBytes(node.OwnSizeBytes);
                FileCount = node.TotalFileCount;
                ActualFileCount = node.TotalActualFileCount;
                HistoryFileCount = node.TotalHistoryFileCount;
                ObjectCount = node.TotalObjectCount;
                RelationText = node.RelationText;
                SnapshotText = FormatSnapshotText(node);
                if (node.Kind == NodeKind.File || node.Kind == NodeKind.StorageFile)
                {
                    BarBrush = CreateBarBrush(Color.FromRgb(91, 178, 132), Color.FromRgb(47, 137, 94));
                    BarBorderBrush = new SolidColorBrush(Color.FromRgb(36, 111, 74));
                }
                else if (node.Kind == NodeKind.StorageDirectory)
                {
                    BarBrush = CreateBarBrush(Color.FromRgb(229, 166, 73), Color.FromRgb(197, 128, 43));
                    BarBorderBrush = new SolidColorBrush(Color.FromRgb(161, 99, 32));
                }
                else
                {
                    BarBrush = CreateBarBrush(Color.FromRgb(76, 139, 214), Color.FromRgb(48, 103, 175));
                    BarBorderBrush = new SolidColorBrush(Color.FromRgb(39, 88, 151));
                }

                var metricSize = FilesSizeReportWindow.GetEffectiveSizeBytes(node);
                ChartSizeText = PrimarySizeText;
                ChartMetricText = PrimarySourceText;

                var ratio = maxSizeBytes <= 0 ? 0 : metricSize / (double)maxSizeBytes;
                BarHeight = Math.Max(4.0, Math.Round(ratio * MaxBarHeight));

                TooltipText = DisplayName + Environment.NewLine +
                              "Тип: " + KindText + Environment.NewLine +
                              "Основной размер: " + PrimarySizeText + " (" + PrimarySizeBytesText + ")" + Environment.NewLine +
                              "Источник размера: " + PrimarySourceText + Environment.NewLine +
                              "Pilot-Storage FileInfo.Length: " + StorageSizeText + " (" + StorageSizeBytesText + ")" + Environment.NewLine +
                              "Pilot SDK/БД IFile.Size актуальный: " + ActualSizeText + " (" + ActualSizeBytesText + ")" + Environment.NewLine +
                              "Pilot SDK/БД IFile.Size всего: " + SizeText + " (" + SizeBytesText + ")" + Environment.NewLine +
                              "Разница SDK акт. - Storage: " + MismatchText + " (" + MismatchBytesText + ")" + Environment.NewLine +
                              "Размер общий: " + SizeText + " (" + SizeBytesText + ")" + Environment.NewLine +
                              "Актуальный размер: " + ActualSizeText + " (" + ActualSizeBytesText + ")" + Environment.NewLine +
                              "История версий: " + HistorySizeText + " (" + HistorySizeBytesText + ")" + Environment.NewLine +
                              "Собственный размер: " + OwnSizeText + " (" + OwnSizeBytesText + ")" + Environment.NewLine +
                              "Файлов: " + FileCount.ToString("N0", CultureInfo.InvariantCulture) +
                              " (акт. " + ActualFileCount.ToString("N0", CultureInfo.InvariantCulture) +
                              ", ист. " + HistoryFileCount.ToString("N0", CultureInfo.InvariantCulture) + ")" + Environment.NewLine +
                              "Файлов Storage: " + node.StorageFileCount.ToString("N0", CultureInfo.InvariantCulture) + Environment.NewLine +
                              "Объектов: " + ObjectCount.ToString("N0", CultureInfo.InvariantCulture) +
                              (string.IsNullOrWhiteSpace(SnapshotText) ? "" : Environment.NewLine + "Версия: " + SnapshotText) +
                              (string.IsNullOrWhiteSpace(RelationText) ? "" : Environment.NewLine + "Связи: " + RelationText) +
                              (string.IsNullOrWhiteSpace(StorageStateText) ? "" : Environment.NewLine + "Storage state: " + StorageStateText) +
                              (string.IsNullOrWhiteSpace(StoragePathText) ? "" : Environment.NewLine + "Storage path: " + StoragePathText) + Environment.NewLine +
                              "Путь: " + PathText;
            }

            public PilotSizeNode Node { get; private set; }
            public string DisplayName { get; private set; }
            public string ShortName { get; private set; }
            public string PathText { get; private set; }
            public string TypeTitle { get; private set; }
            public string KindText { get; private set; }
            public string PrimarySizeText { get; private set; }
            public string PrimarySizeBytesText { get; private set; }
            public string PrimarySourceText { get; private set; }
            public string ChartSizeText { get; private set; }
            public string ChartMetricText { get; private set; }
            public string SizeText { get; private set; }
            public string SizeBytesText { get; private set; }
            public string ActualSizeText { get; private set; }
            public string ActualSizeBytesText { get; private set; }
            public string HistorySizeText { get; private set; }
            public string HistorySizeBytesText { get; private set; }
            public string StorageSizeText { get; private set; }
            public string StorageSizeBytesText { get; private set; }
            public string MismatchText { get; private set; }
            public string MismatchBytesText { get; private set; }
            public string StorageStateText { get; private set; }
            public string StoragePathText { get; private set; }
            public string OwnSizeText { get; private set; }
            public string OwnSizeBytesText { get; private set; }
            public int FileCount { get; private set; }
            public int ActualFileCount { get; private set; }
            public int HistoryFileCount { get; private set; }
            public int ObjectCount { get; private set; }
            public string RelationText { get; private set; }
            public string SnapshotText { get; private set; }
            public double BarHeight { get; private set; }
            public Brush BarBrush { get; private set; }
            public Brush BarBorderBrush { get; private set; }
            public string TooltipText { get; private set; }

            private static string GetKindText(PilotSizeNode node)
            {
                if (node.Kind == NodeKind.File || node.Kind == NodeKind.StorageFile)
                    return node.TypeTitle;

                if (node.Kind == NodeKind.StorageDirectory)
                    return "Pilot-Storage папка";

                return node.Kind == NodeKind.Virtual ? "Корень" : "Объект";
            }

            private static string FormatSnapshotText(PilotSizeNode node)
            {
                if (node.Kind != NodeKind.File)
                    return "";

                var status = node.IsActualFile ? "Актуальная" : "История";
                var created = node.FileSnapshotCreated == DateTime.MinValue
                    ? ""
                    : node.FileSnapshotCreated.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

                var text = string.IsNullOrWhiteSpace(created) ? status : status + ", " + created;
                if (!string.IsNullOrWhiteSpace(node.FileSnapshotReason))
                    text += ", " + node.FileSnapshotReason;

                return text;
            }

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
            private static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };

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

            public static string FormatBytes(long bytes)
            {
                return bytes.ToString("N0", CultureInfo.CurrentCulture) + " байт";
            }

            public static string FormatSigned(long bytes)
            {
                if (bytes == 0)
                    return Format(0);

                return (bytes > 0 ? "+" : "-") + Format(Math.Abs(bytes));
            }

            public static string FormatSignedBytes(long bytes)
            {
                if (bytes == 0)
                    return FormatBytes(0);

                return (bytes > 0 ? "+" : "-") + FormatBytes(Math.Abs(bytes));
            }
        }
    }
}
