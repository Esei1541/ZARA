using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Zara.Application.Updates;
using Zara.Desktop.Updates;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class ReleaseUpdateViewModelTests
{
    [TestMethod]
    public async Task AvailableReleaseShowsVersionsAndUnmodifiedNotes()
    {
        var updates = new FakeUpdates();
        using var viewModel = Create(updates);
        await viewModel.RefreshAsync();

        Assert.AreEqual("v1.0.0", viewModel.CurrentVersion);
        Assert.AreEqual("v1.0.1", viewModel.LatestVersion);
        Assert.AreEqual("설치 가능한 업데이트가 있습니다.", viewModel.StatusMessage);
        Assert.AreEqual(updates.Release.Notes, viewModel.ReleaseNotes);
        Assert.IsTrue(viewModel.CanInstall);
    }

    [TestMethod]
    public async Task EqualOrLowerRemoteVersionIsCurrentAndNotInstallable()
    {
        foreach (var version in new[] { new Version(1, 0, 0), new Version(0, 9, 9) })
        {
            var updates = new FakeUpdates();
            updates.Release = updates.Release with { Version = version };
            using var viewModel = Create(updates);
            await viewModel.RefreshAsync();
            Assert.AreEqual("최신 버전입니다.", viewModel.StatusMessage);
            Assert.AreEqual("v1.0.0", viewModel.CurrentVersion);
            Assert.IsFalse(viewModel.HasUpdate);
            Assert.IsFalse(viewModel.InstallCommand.CanExecute(null));
        }
    }

    [TestMethod]
    public async Task FailedRefreshClearsPreviouslyAvailableUpdateAndKeepsSpecificCode()
    {
        var updates = new FakeUpdates();
        using var viewModel = Create(updates);
        await viewModel.RefreshAsync();
        updates.Check = _ => throw new UpdateException("UPD-GH-HTTP-503");
        await viewModel.RefreshAsync();

        Assert.AreEqual("지금은 업데이트를 확인할 수 없습니다.", viewModel.StatusMessage);
        Assert.AreEqual("UPD-GH-HTTP-503", viewModel.ErrorCode);
        Assert.AreEqual("GitHub 서버 오류", viewModel.ErrorDescription);
        Assert.IsFalse(viewModel.HasUpdate);
        Assert.AreEqual(string.Empty, viewModel.ReleaseNotes);
        Assert.IsTrue(viewModel.RefreshCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task StartupAndTabEntrySharePendingCheckAndPromptOnlyOnce()
    {
        var updates = new FakeUpdates();
        var completion = new TaskCompletionSource<ReleaseUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        updates.Check = _ => completion.Task;
        int prompts = 0;
        using var viewModel = Create(updates, () => { prompts++; return false; });
        Task startup = viewModel.CheckOnStartupAsync();
        Task entry = viewModel.RefreshAsync();
        Assert.AreEqual(1, updates.Checks);
        Assert.IsFalse(viewModel.CanInteract);
        completion.SetResult(updates.Release);
        await Task.WhenAll(startup, entry);
        await viewModel.CheckOnStartupAsync();
        await viewModel.TryShowStartupPromptAsync();
        Assert.AreEqual(1, prompts);
        Assert.AreEqual(0, updates.Installs);
    }

    [TestMethod]
    public async Task LockedStartupDefersPromptUntilUnlockAndAcceptOpensTabThenInstalls()
    {
        var updates = new FakeUpdates();
        bool unlocked = false;
        int prompts = 0;
        var calls = new List<string>();
        updates.Install = (_, _) => { calls.Add("install"); return Task.FromResult(true); };
        using var viewModel = new ReleaseUpdateViewModel(updates,
            () => { prompts++; return true; }, () => unlocked, () => calls.Add("tab"));
        await viewModel.CheckOnStartupAsync();
        Assert.AreEqual(0, prompts);
        unlocked = true;
        await viewModel.TryShowStartupPromptAsync();
        await viewModel.TryShowStartupPromptAsync();
        Assert.AreEqual(1, prompts);
        Assert.HasCount(2, calls);
        Assert.AreEqual("tab", calls[0]);
        Assert.AreEqual("install", calls[1]);
        Assert.AreEqual("Windows에 설치 실행을 요청했습니다.", viewModel.OperationMessage);
    }

    [TestMethod]
    public async Task StartupFailureIsSilentAndExplicitRetryCanRecover()
    {
        var updates = new FakeUpdates { Check = _ => throw new UpdateException("UPD-NET-TIMEOUT") };
        int prompts = 0;
        using var viewModel = Create(updates, () => { prompts++; return true; });
        await viewModel.CheckOnStartupAsync();
        Assert.AreEqual(0, prompts);
        Assert.AreEqual("UPD-NET-TIMEOUT", viewModel.ErrorCode);
        updates.Check = null;
        await viewModel.RefreshAsync();
        Assert.IsFalse(viewModel.HasError);
        Assert.IsTrue(viewModel.CanInstall);
    }

    [TestMethod]
    public async Task InstallInProgressPreventsDuplicateRequestsAndRefresh()
    {
        var updates = new FakeUpdates();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        updates.Install = (_, _) => completion.Task;
        using var viewModel = Create(updates, () => true);
        await viewModel.RefreshAsync();
        Task install = viewModel.InstallAsync();
        await viewModel.RefreshAsync();
        await viewModel.InstallAsync();
        Assert.IsFalse(viewModel.CanInteract);
        Assert.AreEqual(1, updates.Checks);
        Assert.AreEqual(1, updates.Installs);
        completion.SetResult(false);
        await install;
        Assert.AreEqual("설치를 취소했습니다.", viewModel.OperationMessage);
        Assert.IsTrue(viewModel.CanInstall);
    }

    [TestMethod]
    public async Task DisposalCancelsPendingCheckAndPreventsStartupPrompt()
    {
        var updates = new FakeUpdates();
        updates.Check = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return updates.Release;
        };
        int prompts = 0;
        using var viewModel = Create(updates, () => { prompts++; return true; });
        Task startup = viewModel.CheckOnStartupAsync();
        viewModel.Dispose();
        await startup;
        Assert.AreEqual(0, prompts);
        Assert.IsFalse(viewModel.CanInteract);
    }

    [STATestMethod]
    public async Task UpdateTabUsesSharedHeadingStyleAndDisplaysRealBindingsInAllConfigurations()
    {
        var updates = new FakeUpdates();
        using var updateViewModel = Create(updates);
        using var mainViewModel = new MainWindowViewModel(true, (_, _) => Task.CompletedTask
#if DEBUG
            , () => Task.CompletedTask, () => Task.CompletedTask
#endif
            );
        var window = new MainWindow(mainViewModel, releaseUpdates: updateViewModel)
        {
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        try
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.SelectUpdateTab();
            await updateViewModel.RefreshAsync();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            window.UpdateLayout();
            var tab = (TabItem)window.MainTabs.SelectedItem;
            var view = (ReleaseUpdateView)tab.Content;
            Assert.AreEqual("업데이트", tab.Header);
            Assert.AreSame(view.FindResource("Zara.SectionTitle"), view.SectionTitle.Style);
            Assert.AreEqual("업데이트", view.SectionTitle.Text);
            Assert.AreEqual("v1.0.0", view.CurrentVersionText.Text);
            Assert.AreEqual("설치 가능한 업데이트가 있습니다.", view.StatusText.Text);
            Assert.AreEqual(updates.Release.Notes, view.ReleaseNotesText.Text);
            Assert.AreEqual(Visibility.Visible, view.InstallButton.Visibility);
            Assert.IsTrue(view.InstallButton.IsEnabled);
            Point buttonPosition = view.InstallButton.TranslatePoint(new Point(), view);
            Assert.IsLessThanOrEqualTo(view.ActualWidth, buttonPosition.X + view.InstallButton.ActualWidth);

            updates.Check = _ => throw new UpdateException("UPD-NET-OFFLINE");
            await updateViewModel.RefreshAsync();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.AreEqual("지금은 업데이트를 확인할 수 없습니다.", view.StatusText.Text);
            Assert.AreEqual("UPD-NET-OFFLINE", view.ErrorCodeText.Text);
            Assert.AreEqual(Visibility.Collapsed, view.InstallButton.Visibility);
            Assert.AreEqual(Visibility.Collapsed, view.ReleaseNotesPanel.Visibility);
        }
        finally
        {
            window.Close();
        }
    }

    private static ReleaseUpdateViewModel Create(FakeUpdates updates, Func<bool>? confirm = null) =>
        new(updates, confirm ?? (() => false), () => true, () => { });

    private sealed class FakeUpdates : IReleaseUpdates
    {
        public Version CurrentVersion => new(1, 0, 0);
        internal ReleaseUpdate Release { get; set; } = new(new Version(1, 0, 1),
            "v1.0.1\n\n- 첫 번째 변경\n- 두 번째 변경", new Uri("https://github.com/Esei1541/ZARA/releases/download/v1.0.1/ZARA-1.0.1-win-x64-Setup.exe"),
            new string('a', 64), 12);
        internal Func<CancellationToken, Task<ReleaseUpdate>>? Check { get; set; }
        internal Func<ReleaseUpdate, CancellationToken, Task<bool>>? Install { get; set; }
        internal int Checks { get; private set; }
        internal int Installs { get; private set; }

        public Task<ReleaseUpdate> CheckAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            return Check?.Invoke(cancellationToken) ?? Task.FromResult(Release);
        }

        public Task<bool> InstallAsync(ReleaseUpdate release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            Installs++;
            return Install?.Invoke(release, cancellationToken) ?? Task.FromResult(true);
        }
    }
}
