namespace Zara.Application.Updates;

/// <summary>A published stable release and its Windows installer.</summary>
public sealed record ReleaseUpdate(Version Version, string Notes, Uri InstallerUri, string Sha256, long Size);
