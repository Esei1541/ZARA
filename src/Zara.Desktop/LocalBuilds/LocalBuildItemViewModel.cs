#if LOCAL_BUILD_UPDATES
using Zara.Application.LocalBuilds;

namespace Zara.Desktop.LocalBuilds;

/// <summary>Projects one local build catalog entry for the build selection table.</summary>
internal sealed class LocalBuildItemViewModel
{
    internal LocalBuildItemViewModel(LocalBuildEntry entry, bool isCurrent)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        IsCurrent = isCurrent;
    }

    internal LocalBuildEntry Entry { get; }
    public string BuildId => Entry.Build.BuildId;
    public string VersionName => string.IsNullOrWhiteSpace(Entry.Build.VersionName) ? "확인 불가" : Entry.Build.VersionName;
    public string Configuration => string.IsNullOrWhiteSpace(Entry.Build.Configuration) ? "확인 불가" : Entry.Build.Configuration;
    public string CreatedAtText => Entry.Build.CreatedAt == DateTimeOffset.MinValue ? "확인 불가" : $"{Entry.Build.CreatedAt:yyyy-MM-dd HH:mm:ss}";
    public string Branch => string.IsNullOrWhiteSpace(Entry.Build.Branch) ? "확인 불가" : Entry.Build.Branch;
    public string Commit => string.IsNullOrWhiteSpace(Entry.Build.Commit) ? "확인 불가" : Entry.Build.Commit;
    public string? Problem => Entry.Problem;
    public bool IsCurrent { get; }
    public string CurrentMarker => IsCurrent ? "현재" : string.Empty;
    public bool CanInstall => Entry.CanInstall && !IsCurrent;
}
#endif
