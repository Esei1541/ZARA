using System.Text.Json;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Persists per-user desktop execution settings under the Windows local application-data folder.
/// </summary>
/// <remarks>
/// Reads and writes are serialized within the process. Writes use a temporary file in the target
/// directory and replace or move it into place only after serialization and flushing complete.
/// Invalid JSON is moved aside before the default settings are returned.
/// </remarks>
public sealed class WindowsDesktopRestartSettingsStore : IDisposable
{
    private const string ApplicationDirectoryName = "ZARA";
    private const string SettingsFileName = "settings.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsDirectoryPath;
    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _accessGate = new(initialCount: 1, maxCount: 1);
    private int _disposeState;

    /// <summary>
    /// Initializes a store rooted at <c>%LocalAppData%\ZARA</c> for the current Windows user.
    /// </summary>
    public WindowsDesktopRestartSettingsStore()
        : this(GetDefaultSettingsDirectoryPath())
    {
    }

    internal WindowsDesktopRestartSettingsStore(string settingsDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectoryPath);
        if (!Path.IsPathFullyQualified(settingsDirectoryPath))
        {
            throw new ArgumentException(
                "An absolute settings directory path is required.",
                nameof(settingsDirectoryPath));
        }

        _settingsDirectoryPath = Path.GetFullPath(settingsDirectoryPath);
        _settingsFilePath = Path.Combine(_settingsDirectoryPath, SettingsFileName);
    }

    /// <summary>
    /// Loads the persisted settings, returning the default value when the document is absent or
    /// after invalid JSON has been quarantined.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the store or reading the document.</param>
    /// <returns>The persisted settings or <see cref="DesktopRestartSettings.Default" />.</returns>
    public async Task<DesktopRestartSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!File.Exists(_settingsFilePath))
            {
                return DesktopRestartSettings.Default;
            }

            try
            {
                await using var stream = new FileStream(
                    _settingsFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                DesktopRestartSettings? settings = await JsonSerializer
                    .DeserializeAsync<DesktopRestartSettings>(
                        stream,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (settings is null)
                {
                    throw new JsonException("The settings document did not contain an object.");
                }

                return settings;
            }
            catch (JsonException)
            {
                QuarantineCorruptSettings();
                return DesktopRestartSettings.Default;
            }
        }
        finally
        {
            _accessGate.Release();
        }
    }

    /// <summary>
    /// Atomically persists the supplied settings in the current user's local application data.
    /// </summary>
    /// <param name="settings">The complete settings snapshot to persist.</param>
    /// <param name="cancellationToken">Cancels waiting, serialization, or flushing before commit.</param>
    public async Task SaveAsync(
        DesktopRestartSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        await _accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryFilePath = null;
        try
        {
            ThrowIfDisposed();
            Directory.CreateDirectory(_settingsDirectoryPath);
            temporaryFilePath = Path.Combine(
                _settingsDirectoryPath,
                $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer
                    .SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_settingsFilePath))
            {
                File.Replace(
                    temporaryFilePath,
                    _settingsFilePath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryFilePath, _settingsFilePath);
            }

            temporaryFilePath = null;
        }
        finally
        {
            if (temporaryFilePath is not null)
            {
                TryDeleteTemporaryFile(temporaryFilePath);
            }

            _accessGate.Release();
        }
    }

    /// <summary>
    /// Releases synchronization resources owned by the store.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _accessGate.Wait();
        _accessGate.Release();
        _accessGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string GetDefaultSettingsDirectoryPath()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(localApplicationData) ||
            !Path.IsPathFullyQualified(localApplicationData))
        {
            throw new InvalidOperationException(
                "Windows did not provide an absolute local application-data path.");
        }

        return Path.Combine(localApplicationData, ApplicationDirectoryName);
    }

    private void QuarantineCorruptSettings()
    {
        string quarantinePath = Path.Combine(
            _settingsDirectoryPath,
            $"settings.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json");
        File.Move(_settingsFilePath, quarantinePath);
    }

    private static void TryDeleteTemporaryFile(string temporaryFilePath)
    {
#pragma warning disable CA1031 // Cleanup must not replace the original write or cancellation failure.
        try
        {
            File.Delete(temporaryFilePath);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
}
