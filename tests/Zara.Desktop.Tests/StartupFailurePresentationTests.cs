using Zara.Application.Startup;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class StartupFailurePresentationTests
{
    private const string SensitiveMarker = "secret-native-message-token";

    [TestMethod]
    [DataRow(StartupFailureKind.AccessDenied, true)]
    [DataRow(StartupFailureKind.ElevationCancelled, false)]
    [DataRow(StartupFailureKind.ServiceMissing, false)]
    [DataRow(StartupFailureKind.BinaryMissing, false)]
    [DataRow(StartupFailureKind.ServiceDisabled, false)]
    [DataRow(StartupFailureKind.ServiceDeleting, false)]
    [DataRow(StartupFailureKind.IdentityMismatch, false)]
    [DataRow(StartupFailureKind.StartFailed, true)]
    [DataRow(StartupFailureKind.ServiceStopped, true)]
    [DataRow(StartupFailureKind.TimedOut, true)]
    [DataRow(StartupFailureKind.ConnectionFailed, true)]
    [DataRow(StartupFailureKind.RegistrationRejected, false)]
    [DataRow(StartupFailureKind.Unexpected, false)]
    public void StartupFailureUsesSafeTextAndCorrectRetryPolicy(
        StartupFailureKind kind,
        bool canRetry)
    {
        var exception = new DesktopStartupException(kind, SensitiveMarker, nativeErrorCode: 1067);

        StartupFailurePresentation presentation = StartupFailurePresentation.FromException(exception);

        Assert.IsFalse(string.IsNullOrWhiteSpace(presentation.Message));
        Assert.AreEqual(canRetry, presentation.CanRetry);
        StringAssert.Contains(presentation.Details, kind.ToString());
        StringAssert.Contains(presentation.Details, "1067");
        Assert.IsFalse(presentation.Message.Contains(SensitiveMarker, StringComparison.Ordinal));
        Assert.IsFalse(presentation.Details.Contains(SensitiveMarker, StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnrecognizedExceptionDoesNotExposeRawMessage()
    {
        StartupFailurePresentation presentation = StartupFailurePresentation.FromException(
            new InvalidOperationException(SensitiveMarker));

        Assert.IsFalse(presentation.CanRetry);
        Assert.IsFalse(presentation.Message.Contains(SensitiveMarker, StringComparison.Ordinal));
        Assert.IsFalse(presentation.Details.Contains(SensitiveMarker, StringComparison.Ordinal));
    }

    [TestMethod]
    public void StoppedServiceShowsStructuredCodesWithoutRawMessage()
    {
        var exception = new DesktopStartupException(
            StartupFailureKind.ServiceStopped, SensitiveMarker, nativeErrorCode: 1067)
        {
            ServiceExitCode = 42,
        };

        StartupFailurePresentation presentation = StartupFailurePresentation.FromException(exception);

        StringAssert.Contains(presentation.Details, "Windows 오류 코드: 1067");
        StringAssert.Contains(presentation.Details, "서비스 오류 코드: 42");
        Assert.IsFalse(presentation.Message.Contains(SensitiveMarker, StringComparison.Ordinal));
        Assert.IsFalse(presentation.Details.Contains(SensitiveMarker, StringComparison.Ordinal));
    }
}
