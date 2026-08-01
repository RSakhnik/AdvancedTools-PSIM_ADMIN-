using System.ComponentModel;
using System.Windows;

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
            InitializationText.Foreground = System.Windows.Media.Brushes.Black;
            InitializationProgress.Visibility = Visibility.Visible;
            InitializationOverlay.Visibility = Visibility.Visible;
        }

        public void ShowInitializationError(string message)
        {
            InitializationText.Text = "Ошибка инициализации BIM Bridge:\n\n" + message;
            InitializationText.Foreground = System.Windows.Media.Brushes.Firebrick;
            InitializationProgress.Visibility = Visibility.Collapsed;
            InitializationOverlay.Visibility = Visibility.Visible;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
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
