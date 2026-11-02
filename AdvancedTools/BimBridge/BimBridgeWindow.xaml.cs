using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AdvancedTools.BimBridge
{
    public partial class BimBridgeWindow : Window
    {
        private bool _allowClose;

        public BimBridgeWindow()
        {
            InitializeComponent();
        }

        public void SetViewModel(BimBridgeViewModel viewModel)
        {
            DataContext = viewModel;
            InitializationOverlay.Visibility = Visibility.Collapsed;
        }

        public void ShowInitialization(string message)
        {
            InitializationText.Text = message;
            InitializationText.Foreground = new SolidColorBrush(Color.FromRgb(82, 100, 119));
            InitializationProgress.Visibility = Visibility.Visible;
            InitializationOverlay.Visibility = Visibility.Visible;
        }

        public void ShowInitializationError(string message)
        {
            InitializationText.Text = "Не удалось инициализировать BIM Bridge:\n\n" + message;
            InitializationText.Foreground = Brushes.Firebrick;
            InitializationProgress.Visibility = Visibility.Collapsed;
            InitializationOverlay.Visibility = Visibility.Visible;
        }

        private void ProjectNameTextBox_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var viewModel = DataContext as BimBridgeViewModel;
            if (viewModel?.AddProjectCommand.CanExecute(null) != true) return;
            viewModel.AddProjectCommand.Execute(null);
            e.Handled = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                // Keep a taskbar entry: reopening must not require the blocked Pilot menu.
                WindowState = WindowState.Minimized;
                return;
            }
            base.OnClosing(e);
        }

        public void ClosePermanently()
        {
            _allowClose = true;
            Close();
        }
    }
}
