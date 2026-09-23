#if LOCAL_BUILD_UPDATES
using UserControl = System.Windows.Controls.UserControl;

namespace Zara.Desktop.LocalBuilds;

internal sealed partial class LocalBuildView : UserControl
{
    internal LocalBuildView(LocalBuildViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }
}
#endif
