using System;
using System.Windows;

namespace AdvancedTools.BimBridge
{
    public partial class ResourceLimitsWindow : Window
    {
        public ResourceLimitsWindow(ResourceLimitsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            viewModel.RequestClose += OnRequestClose;
            Closed += (sender, args) => viewModel.RequestClose -= OnRequestClose;
        }

        private void OnRequestClose(bool saved)
        {
            Close();
        }
    }
}
