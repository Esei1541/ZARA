using System.Text.Json;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Persists user-specific time rules, emergency-unlock settings, and reservations under the local
/// Windows application-data directory.
/// </summary>
/// <remarks>
/// A committed document is copied to a last-known-good companion file after each successful write.
/// Invalid primary JSON or semantically invalid settings are quarantined, then the companion file is
/// used when available. In-memory emergency-unlock state is intentionally not part of this document.
/// </remarks>
public sealed class WindowsUsagePolicySettingsStore : IUsagePolicySettingsStore, IDisposable
{
    private const string ApplicationDirectoryName = "ZARA";
    private const string SettingsFileName = "usage-policy.json";
    private const string LastKnownGoodFileName = "usage-policy.last-known-good.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsDirectoryPath;
    private readonly string _settingsFilePath;
    private readonly string _lastKnownGoodFilePath;
    private readonly SemaphoreSlim _accessGate = new(initialCount: 1, maxCount: 1);
    private int _disposeState;

    /// <summary>
    /// Initializes a store rooted at <c>%LocalAppData%\ZARA</c> for the current Windows user.
    /// </summary>
    public WindowsUsagePolicySettingsStore()
        : this(GetDefaultSettingsDirectoryPath())
    {
    }

    internal WindowsUsagePolicySettingsStore(string settingsDirectoryPath)
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
        _lastKnownGoodFilePath = Path.Combine(_settingsDirectoryPath, LastKnownGoodFileName);
    }

    /// <inheritdoc />
    public async Task<UsagePolicySettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!File.Exists(_settingsFilePath))
            {
                return await LoadLastKnownGoodOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await ReadSettingsAsync(_settingsFilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCorruptDocumentException(exception))
            {
                QuarantineCorruptDocument(_settingsFilePath, SettingsFileName);
                return await LoadLastKnownGoodOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _accessGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        UsagePolicySettings settings,
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
                    .SerializeAsync(
                        stream,
                        PersistedUsagePolicySettings.FromSettings(settings),
                        SerializerOptions,
                        cancellationToken)
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
            TryWriteLastKnownGoodCopy();
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

    private async Task<UsagePolicySettings> LoadLastKnownGoodOrDefaultAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_lastKnownGoodFilePath))
        {
            return UsagePolicySettings.Default;
        }

        try
        {
            return await ReadSettingsAsync(_lastKnownGoodFilePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCorruptDocumentException(exception))
        {
            QuarantineCorruptDocument(_lastKnownGoodFilePath, LastKnownGoodFileName);
            return UsagePolicySettings.Default;
        }
    }

    private static async Task<UsagePolicySettings> ReadSettingsAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        PersistedUsagePolicySettings? document = await JsonSerializer
            .DeserializeAsync<PersistedUsagePolicySettings>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
        return document?.ToSettings() ??
            throw new JsonException("The usage-policy document did not contain an object.");
    }

    private static bool IsCorruptDocumentException(Exception exception) =>
        exception is JsonException or ArgumentException or FormatException or NotSupportedException;

    private sealed class PersistedUsagePolicySettings
    {
        public WeeklyUsageRestrictionSchedule? WeeklySchedule { get; init; }

        public EmergencyUnlockSettings? EmergencyUnlock { get; init; }

        public EmergencyUnlockUsage? EmergencyUnlockUsage { get; init; }

        public List<OutOfHoursReservation>? Reservations { get; init; }

        public static PersistedUsagePolicySettings FromSettings(UsagePolicySettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return new PersistedUsagePolicySettings
            {
                WeeklySchedule = settings.WeeklySchedule,
                EmergencyUnlock = settings.EmergencyUnlock,
                EmergencyUnlockUsage = settings.EmergencyUnlockUsage,
                Reservations = settings.Reservations.ToList(),
            };
        }

        public UsagePolicySettings ToSettings() =>
            new(
                WeeklySchedule ?? throw new JsonException(
                    "The usage-policy document did not contain a weekly schedule."),
                EmergencyUnlock ?? throw new JsonException(
                    "The usage-policy document did not contain emergency-unlock settings."),
                Reservations ?? throw new JsonException(
                    "The usage-policy document did not contain reservations."),
                EmergencyUnlockUsage);
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

    private void QuarantineCorruptDocument(string sourcePath, string name)
    {
        Directory.CreateDirectory(_settingsDirectoryPath);
        string quarantinePath = Path.Combine(
            _settingsDirectoryPath,
            $"{Path.GetFileNameWithoutExtension(name)}.corrupt-" +
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json");
        File.Move(sourcePath, quarantinePath);
    }

    private void TryWriteLastKnownGoodCopy()
    {
        string temporaryBackupPath = Path.Combine(
            _settingsDirectoryPath,
            $".{LastKnownGoodFileName}.{Guid.NewGuid():N}.tmp");

#pragma warning disable CA1031 // The primary commit has already succeeded; backup failure cannot undo it.
        try
        {
            File.Copy(_settingsFilePath, temporaryBackupPath, overwrite: false);
            File.Move(temporaryBackupPath, _lastKnownGoodFilePath, overwrite: true);
        }
        catch (Exception)
        {
            TryDeleteTemporaryFile(temporaryBackupPath);
        }
#pragma warning restore CA1031
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
