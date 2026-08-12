using System.Windows;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop;

/// <summary>
/// Collects UI form values for a new time-outside-use reservation without applying product rules.
/// </summary>
public partial class ReservationEditorDialog : Window
{
    private readonly ReservationEditorViewModel _viewModel = new();

    public ReservationEditorDialog()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>Gets the form result after the user completes the dialog.</summary>
    internal ReservationDraft? Draft { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryCreateDraft(
                out ReservationDraft? draft,
                out string validationMessage) ||
            draft is null)
        {
            System.Windows.MessageBox.Show(
                this,
                validationMessage,
                "시간 외 사용 예약",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Draft = draft;
        DialogResult = true;
    }
}
