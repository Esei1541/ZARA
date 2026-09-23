namespace Zara.Desktop.Updates;

internal partial class ReleaseUpdateView : System.Windows.Controls.UserControl
{
    internal ReleaseUpdateView(ReleaseUpdateViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }
}
