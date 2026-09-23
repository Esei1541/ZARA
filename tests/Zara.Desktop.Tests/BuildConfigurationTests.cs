using System.Collections;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows.Controls;
#if LOCAL_BUILD_UPDATES
using Zara.Application.LocalBuilds;
#endif
using Zara.Application.Locking;
using Zara.Application.UsagePolicy;
using Zara.Desktop.Overlays;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class BuildConfigurationTests
{
    [STATestMethod]
    public void ConfigurationFeatureMatrixMatchesDebugStagingAndRelease()
    {
        Assembly desktopAssembly = typeof(App).Assembly;
        string configuration = desktopAssembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? string.Empty;
        bool expectsDevelopmentControls = string.Equals(
            configuration,
            "Debug",
            StringComparison.Ordinal);
        bool expectsLocalBuildUpdates = configuration is "Debug" or "Staging";

        using var viewModel = new MainWindowViewModel(
            restartOnExitWhenUnlocked: false,
            saveExecutionSettings: (_, _) => Task.CompletedTask
#if DEBUG
            , requestLock: () => Task.CompletedTask,
            requestDevelopmentUnlock: () => Task.CompletedTask
#endif
            );
        var mainWindow = new MainWindow(
            viewModel
#if LOCAL_BUILD_UPDATES
            , new EmptyLocalBuildUpdates()
#endif
            );
        var overlay = new OverlayWindow(
            () => Task.CompletedTask,
            () => Task.CompletedTask
#if DEBUG
            , () => Task.CompletedTask
#endif
            );

        try
        {
            var shellActions = (Panel)mainWindow.FindName("ShellActions");
            var lockActions = (WrapPanel)overlay.FindName("LockActions");
            bool hasBuildTab = mainWindow.MainTabs.Items
                .OfType<TabItem>()
                .Any(tab => string.Equals(tab.Header as string, "빌드", StringComparison.Ordinal));
            bool hasLocalBuildType = desktopAssembly.GetType(
                "Zara.Desktop.LocalBuilds.LocalBuildViewModel",
                throwOnError: false) is not null;

            Assert.AreEqual(expectsDevelopmentControls ? 3 : 1, shellActions.Children.Count);
            Assert.AreEqual(expectsDevelopmentControls ? 3 : 2, lockActions.Children.Count);
            Assert.IsNotNull(overlay.FindName("EmergencyUnlockButton"));
            Assert.AreEqual(expectsLocalBuildUpdates, hasBuildTab);
            Assert.AreEqual(expectsLocalBuildUpdates, hasLocalBuildType);
            Assert.AreEqual(expectsLocalBuildUpdates, HasLocalBuildViewResource(desktopAssembly));
            Assert.AreEqual(
                expectsDevelopmentControls,
                typeof(UsagePolicyRuntime).GetMethod(
                    "DisableWeeklyScheduleForDevelopmentAsync") is not null);
            Assert.AreEqual(
                expectsDevelopmentControls,
                typeof(LockRuntimeUseCase).GetMethod(
                    "RequestDevelopmentUnlockAsync") is not null);

            if (!expectsDevelopmentControls)
            {
                AssertHasNoDevelopmentEntryPoints();
            }
        }
        finally
        {
            mainWindow.Close();
            overlay.CloseFromCoordinator();
        }
    }

    private static bool HasLocalBuildViewResource(Assembly assembly)
    {
        string? resourceName = assembly.GetManifestResourceNames().FirstOrDefault(
            name => name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null || assembly.GetManifestResourceStream(resourceName) is not Stream stream)
        {
            return false;
        }

        using (stream)
        using (var reader = new ResourceReader(stream))
        {
            foreach (DictionaryEntry entry in reader)
            {
                if (entry.Key is string key && string.Equals(
                    key,
                    "localbuilds/localbuildview.baml",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AssertHasNoDevelopmentEntryPoints()
    {
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
                    $"{type.Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration} " +
                    $"contains a development entry point: {type.Name}.{member.Name}");
            }
        }
    }

#if LOCAL_BUILD_UPDATES
    private sealed class EmptyLocalBuildUpdates : ILocalBuildUpdates
    {
        public Task<LocalBuildCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalBuildCatalog(
                "C:\\Builds",
                CurrentBuild: null,
                Array.Empty<LocalBuildEntry>(),
                Message: null));

        public Task ChangeDirectoryAsync(
            string directory,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> InstallAsync(
            string buildId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
#endif
}
