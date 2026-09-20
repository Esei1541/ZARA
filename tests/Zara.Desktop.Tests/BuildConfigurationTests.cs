using System.Reflection;
using System.Windows.Controls;
using Zara.Application.Locking;
using Zara.Application.UsagePolicy;
using Zara.Desktop.Overlays;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class BuildConfigurationTests
{
    [STATestMethod]
    public void DevelopmentControlsAndEntryPointsFollowTheBuildConfiguration()
    {
        using var viewModel = new MainWindowViewModel(
            restartOnExitWhenUnlocked: false,
            updateRestartSetting: _ => Task.CompletedTask
#if DEBUG
            , requestLock: () => Task.CompletedTask,
            requestDevelopmentUnlock: () => Task.CompletedTask
#endif
            );
        var mainWindow = new MainWindow(viewModel);
        var overlay = new OverlayWindow(
            () => Task.CompletedTask,
            () => Task.CompletedTask
#if DEBUG
            , () => Task.CompletedTask
#endif
            );

        try
        {
            var shellActions = (StackPanel)mainWindow.FindName("ShellActions");
            var lockActions = (WrapPanel)overlay.FindName("LockActions");
#if DEBUG
            Assert.AreEqual(2, shellActions.Children.Count);
            Assert.AreEqual(3, lockActions.Children.Count);
            Assert.IsNotNull(typeof(UsagePolicyRuntime).GetMethod("DisableWeeklyScheduleForDevelopmentAsync"));
            Assert.IsNotNull(typeof(LockRuntimeUseCase).GetMethod("RequestDevelopmentUnlockAsync"));
#else
            Assert.AreEqual(0, shellActions.Children.Count);
            Assert.AreEqual(2, lockActions.Children.Count);
            Type[] types =
            [
                typeof(App),
                typeof(MainWindowViewModel),
                typeof(OverlayWindow),
                typeof(WpfLockOverlayPort),
                typeof(ILockRuntimeUseCase),
                typeof(LockRuntimeUseCase),
                typeof(UsagePolicyRuntime),
            ];
            foreach (Type type in types)
            {
                foreach (MemberInfo member in type.GetMembers(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    Assert.IsFalse(
                        member.Name.Contains("Development", StringComparison.OrdinalIgnoreCase) ||
                        member.Name.Contains("LockDemo", StringComparison.OrdinalIgnoreCase),
                        $"Release contains development entry point: {type.Name}.{member.Name}");
                }
            }
#endif
        }
        finally
        {
            mainWindow.Close();
            overlay.CloseFromCoordinator();
        }
    }
}
