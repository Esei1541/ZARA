using System.Windows;
using System.Windows.Controls;
using Zara.Application.Startup;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class StartupWindowTests
{
    [STATestMethod]
    public void ServiceStartButtonGrantsOnlyThePendingConsent()
    {
        using var cancellation = new CancellationTokenSource();
        var window = new StartupWindow(cancellation);
        Task<bool> consent = window.ConfirmElevationAsync(cancellation.Token);
        var action = (Button)window.FindName("ActionButton");
        Assert.AreEqual("서비스 시작", action.Content);
        Assert.IsFalse(consent.IsCompleted);

        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.IsTrue(consent.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(consent.Result);
        Assert.IsFalse(cancellation.IsCancellationRequested);
        window.Complete();
        Assert.IsFalse(cancellation.IsCancellationRequested);
    }

    [STATestMethod]
    public void ClosingPreparationCancelsConsentWithoutGrantingPermission()
    {
        using var cancellation = new CancellationTokenSource();
        var window = new StartupWindow(cancellation);
        Task<bool> consent = window.ConfirmElevationAsync(cancellation.Token);

        window.Close();

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(consent.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(consent.Result);
    }

    [STATestMethod]
    public void NonRecoverableFailureOffersNoRetryAndDetailsRemainSelectable()
    {
        using var cancellation = new CancellationTokenSource();
        var window = new StartupWindow(cancellation);
        StartupFailurePresentation failure = StartupFailurePresentation.FromException(
            new DesktopStartupException(StartupFailureKind.ServiceMissing, "missing", 1060));
        Task<bool> choice = window.OfferRetryAsync(failure, cancellation.Token);

        Assert.AreEqual(Visibility.Collapsed, ((Button)window.FindName("ActionButton")).Visibility);
        var details = (TextBox)window.FindName("DetailsText");
        Assert.IsTrue(details.IsReadOnly);
        StringAssert.Contains(details.Text, "1060");
        window.Close();
        Assert.IsTrue(choice.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(choice.Result);
    }
}
