using System.ComponentModel;
using System.Windows;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop;

public partial class MainWindow : Window
{
    internal MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App { IsShuttingDown: false })
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
