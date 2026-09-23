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
    public string CreatedAtMinuteText => Entry.Build.CreatedAt == DateTimeOffset.MinValue ? "확인 불가" : $"{Entry.Build.CreatedAt:yyyy-MM-dd HH:mm}";
    public string Branch => string.IsNullOrWhiteSpace(Entry.Build.Branch) ? "확인 불가" : Entry.Build.Branch;
    public string BranchDisplay => Branch.Length > 7 &&
        Branch[6] == '-' && Branch[..6].All(char.IsDigit)
            ? Branch[7..]
            : Branch;
    public string Commit => string.IsNullOrWhiteSpace(Entry.Build.Commit) ? "확인 불가" : Entry.Build.Commit;
    public string CommitSubject
    {
        get
        {
            string? message = Entry.Build.CommitSubject;
            if (string.IsNullOrWhiteSpace(message))
            {
                return "기록된 커밋 제목이 없습니다.";
            }

            int lineEnd = message.IndexOfAny(['\r', '\n']);
            string title = (lineEnd >= 0 ? message[..lineEnd] : message).Trim();
            return string.IsNullOrWhiteSpace(title) ? "기록된 커밋 제목이 없습니다." : title;
        }
    }
    public string? Problem => Entry.Problem;
    public bool IsCurrent { get; }
    public string StateText => IsCurrent ? "현재 사용 중인 빌드입니다."
        : CanInstall ? "설치 가능" : Problem ?? "설치할 수 없습니다.";
    public bool CanInstall => Entry.CanInstall && !IsCurrent;
}
#endif
