using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class SettingsMessageDialogTests
{
    [STATestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ConfirmationReturnsOnlyTheChosenAction(bool accept)
    {
        var dialog = new SettingsMessageDialog("예약 삭제", "선택한 예약을 삭제하시겠습니까?", "2026.09.25 · 00:00 → 01:00", "삭제")
        {
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
        };
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (accept)
            {
                dialog.AcceptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else
            {
                dialog.Close();
            }
        });

        Assert.AreEqual(accept, dialog.ShowDialog());
        Assert.IsFalse(dialog.AcceptButton.IsDefault);
    }

    [STATestMethod]
    public void InformationalMessageDoesNotLeaveEmptyDetailOrCancelAreas()
    {
        var dialog = new SettingsMessageDialog("기본 설정", "설정이 저장되었습니다.");
        Assert.AreEqual(Visibility.Collapsed, dialog.DetailsPanel.Visibility);
        Assert.AreEqual(Visibility.Collapsed, dialog.CancelButton.Visibility);
        Assert.AreEqual("확인", dialog.AcceptButton.Content);
    }
}
