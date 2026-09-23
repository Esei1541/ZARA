#if LOCAL_BUILD_UPDATES
namespace Zara.Application.LocalBuilds;

/// <summary>Identifies one completed local build independently of its product version.</summary>
public sealed record LocalBuildInfo(
    string BuildId,
    string VersionName,
    string Configuration,
    DateTimeOffset CreatedAt,
    string Branch,
    string Commit,
    string? CommitSubject = null);
#endif
