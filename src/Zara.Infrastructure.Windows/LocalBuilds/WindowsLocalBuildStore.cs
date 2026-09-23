#if LOCAL_BUILD_UPDATES
using System.Security.Cryptography;
using System.Text.Json;
using Zara.Application.LocalBuilds;

namespace Zara.Infrastructure.Windows.LocalBuilds;

/// <summary>Reads completed build directories and validates an installer again before execution.</summary>
public sealed class WindowsLocalBuildStore : ILocalBuildStore
{
    private readonly string _applicationDirectory;
    private readonly string _settingsDirectory;

    public WindowsLocalBuildStore()
        : this(
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZARA"))
    {
    }

    internal WindowsLocalBuildStore(string applicationDirectory, string settingsDirectory)
    {
        _applicationDirectory = Path.GetFullPath(applicationDirectory);
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
    }

    public Task<LocalBuildCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadCoreAsync(cancellationToken), cancellationToken);

    private async Task<LocalBuildCatalog> LoadCoreAsync(CancellationToken cancellationToken)
    {
        (string directory, LocalBuildInfo? current, string? message) =
            await ReadLocationAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(directory))
        {
            return new(directory, current, [], message ?? "빌드 보관 폴더를 선택하십시오.");
        }

        if (!Directory.Exists(directory))
        {
            return new(directory, current, [], "빌드 폴더를 찾을 수 없습니다. 폴더 연결 상태를 확인하거나 다른 폴더를 선택하십시오.");
        }

        EnsurePlainPath(directory);
        var builds = new List<LocalBuildEntry>();
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string manifestPath = Path.Combine(child, "build.json");
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0 || !File.Exists(manifestPath))
            {
                continue;
            }

            string id = Path.GetFileName(child);
            try
            {
                LocalBuildManifest manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                if (!manifest.HasValidInstaller || !string.Equals(manifest.BuildId, id, StringComparison.Ordinal))
                {
                    builds.Add(InvalidEntry(id, "빌드 정보가 올바르지 않습니다."));
                    continue;
                }

                string? problem = null;
                string installer = Path.Combine(child, manifest.InstallerFileName);
                if (!File.Exists(installer))
                {
                    problem = "설치파일이 없습니다.";
                }
                else
                {
                    EnsurePlainPath(installer);
                }

                builds.Add(new(manifest.ToInfo(), problem));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                builds.Add(InvalidEntry(id, "빌드 정보를 읽을 수 없습니다. " + exception.Message));
            }
        }

        LocalBuildEntry[] ordered = builds
            .OrderByDescending(entry => entry.Build.CreatedAt)
            .ThenByDescending(entry => entry.Build.BuildId, StringComparer.Ordinal)
            .ToArray();
        return new(directory, current, ordered, message);
    }

    public async Task ChangeDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        string selected = NormalizeDirectory(directory);
        EnsurePlainPath(selected);
        if (!Directory.Exists(selected))
        {
            throw new DirectoryNotFoundException("선택한 빌드 폴더를 찾을 수 없습니다.");
        }

        // Force an access check before replacing the user's previous selection.
        using (IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(selected).GetEnumerator())
        {
            _ = entries.MoveNext();
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_settingsDirectory);
        string path = Path.Combine(_settingsDirectory, "local-build-settings.json");
        string temporary = Path.Combine(_settingsDirectory, "local-build-settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                new BuildDirectorySettings { BuildsDirectory = selected },
                LocalBuildManifest.JsonOptions);
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<string> ValidateInstallerAsync(string buildId, CancellationToken cancellationToken = default)
    {
        if (!LocalBuildManifest.IsFileName(buildId))
        {
            throw new InvalidDataException("선택한 빌드가 올바르지 않습니다.");
        }

        (string directory, _, _) = await ReadLocationAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("빌드 보관 폴더를 선택하십시오.");
        }

        string buildDirectory = Path.Combine(directory, buildId);
        LocalBuildManifest manifest = await ReadManifestAsync(
            Path.Combine(buildDirectory, "build.json"), cancellationToken).ConfigureAwait(false);
        if (!manifest.HasValidInstaller || !string.Equals(manifest.BuildId, buildId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("선택한 빌드 정보가 바뀌었거나 올바르지 않습니다. 목록을 새로고침하십시오.");
        }

        string installer = Path.Combine(buildDirectory, manifest.InstallerFileName);
        EnsurePlainPath(installer);
        await using var stream = new FileStream(
            installer, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexString(hash), manifest.InstallerSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("설치파일이 생성 당시와 다릅니다. 해당 빌드를 다시 생성하십시오.");
        }

        return installer;
    }

    private async Task<(string Directory, LocalBuildInfo? Current, string? Message)> ReadLocationAsync(
        CancellationToken cancellationToken)
    {
        LocalBuildInfo? current = null;
        string directory = string.Empty;
        string? message = null;
        string identityPath = Path.Combine(_applicationDirectory, "local-build.json");
        if (File.Exists(identityPath))
        {
            try
            {
                LocalBuildManifest identity = await ReadManifestAsync(identityPath, cancellationToken).ConfigureAwait(false);
                if (!identity.HasValidIdentity)
                {
                    throw new InvalidDataException("현재 빌드 정보가 올바르지 않습니다.");
                }

                current = identity.ToInfo();
                if (!string.IsNullOrWhiteSpace(identity.BuildsDirectory))
                {
                    directory = NormalizeDirectory(identity.BuildsDirectory);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                message = "현재 빌드 정보를 읽을 수 없습니다. " + exception.Message;
            }
        }

        string settingsPath = Path.Combine(_settingsDirectory, "local-build-settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                await using var stream = File.OpenRead(settingsPath);
                BuildDirectorySettings? settings = await JsonSerializer.DeserializeAsync<BuildDirectorySettings>(
                    stream, LocalBuildManifest.JsonOptions, cancellationToken).ConfigureAwait(false);
                directory = NormalizeDirectory(settings?.BuildsDirectory ?? string.Empty);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                message = "저장된 빌드 폴더 정보를 읽을 수 없습니다. 폴더를 다시 선택하십시오.";
            }
        }

        return (directory, current, message);
    }

    private static async Task<LocalBuildManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        EnsurePlainPath(path);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LocalBuildManifest>(
            stream, LocalBuildManifest.JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("빌드 정보가 비어 있습니다.");
    }

    private static string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
            directory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("로컬 빌드 폴더의 전체 경로를 선택하십시오.", nameof(directory));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    private static void EnsurePlainPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("연결된 경로의 빌드는 사용할 수 없습니다.");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static LocalBuildEntry InvalidEntry(string id, string problem) =>
        new(new(id, string.Empty, string.Empty, DateTimeOffset.MinValue, string.Empty, string.Empty), problem);

    private sealed class BuildDirectorySettings
    {
        public string BuildsDirectory { get; set; } = string.Empty;
    }
}
#endif
