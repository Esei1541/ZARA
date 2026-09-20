using System.Windows;
using System.Windows.Controls;
using Zara.Desktop.Overlays;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class LockRecoveryPresentationTests
{
    [STATestMethod]
    public void OverlayRecoveryStatusAppearsOnlyWhileRecoveryIsActive()
    {
        var window = new OverlayWindow(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            showDevelopmentControls: true);

        try
        {
            var status = (TextBlock)window.FindName("RecoveryStatus");
            Assert.AreEqual(Visibility.Collapsed, status.Visibility);

            window.SetRecoveryActive(true);
            Assert.AreEqual(Visibility.Visible, status.Visibility);
            Assert.AreEqual("잠금 오류 복원 중...", status.Text);

            window.SetRecoveryActive(false);
            Assert.AreEqual(Visibility.Collapsed, status.Visibility);
        }
        finally
        {
            window.CloseFromCoordinator();
        }
    }

    [STATestMethod]
    public void InitialFailureDialogAcknowledgesWithoutOfferingRetry()
    {
        var dialog = new LockErrorDialog("잠금 오류", "잠금 복구를 시도합니다.", allowRetry: false);

        try
        {
            var retry = (Button)dialog.FindName("RetryButton");
            var confirm = (Button)dialog.FindName("ConfirmButton");
            var message = (TextBlock)dialog.FindName("MessageText");

            Assert.AreEqual(Visibility.Collapsed, retry.Visibility);
            Assert.IsFalse(retry.IsEnabled);
            Assert.IsTrue(confirm.IsDefault);
            Assert.IsTrue(confirm.IsCancel);
            Assert.IsTrue(dialog.Topmost);
            Assert.AreEqual("잠금 복구를 시도합니다.", message.Text);

            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(dialog.RetryRequested);
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    public void ShutdownFailureRetryIsReturnedOnlyWhenRetryButtonIsChosen()
    {
        var dialog = new LockErrorDialog("종료 실패", "PC를 종료하지 못했습니다.", allowRetry: true);

        try
        {
            var retry = (Button)dialog.FindName("RetryButton");
            Assert.AreEqual(Visibility.Visible, retry.Visibility);
            Assert.IsFalse(dialog.RetryRequested);

            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(dialog.RetryRequested);
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    public void ClosingShutdownFailureDialogDoesNotRequestRetry()
    {
        var dialog = new LockErrorDialog("종료 실패", "PC를 종료하지 못했습니다.", allowRetry: true);
        dialog.Close();

        Assert.IsFalse(dialog.RetryRequested);
    }
}
