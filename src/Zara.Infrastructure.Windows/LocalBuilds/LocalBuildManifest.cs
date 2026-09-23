#if LOCAL_BUILD_UPDATES
using System.Text.Json;
using Zara.Application.LocalBuilds;

namespace Zara.Infrastructure.Windows.LocalBuilds;

/// <summary>The build script's catalog and installed-identity document.</summary>
internal sealed class LocalBuildManifest
{
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public int SchemaVersion { get; set; }

    public string BuildId { get; set; } = string.Empty;

    public string VersionName { get; set; } = string.Empty;

    public string Configuration { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string Branch { get; set; } = string.Empty;

    public string Commit { get; set; } = string.Empty;

    public string InstallerFileName { get; set; } = string.Empty;

    public string InstallerSha256 { get; set; } = string.Empty;

    public string BuildsDirectory { get; set; } = string.Empty;

    internal bool HasValidIdentity =>
        SchemaVersion == 1 &&
        IsFileName(BuildId) &&
        !string.IsNullOrWhiteSpace(VersionName) &&
        Configuration is "Debug" or "Staging" &&
        CreatedAt != default &&
        !string.IsNullOrWhiteSpace(Branch) &&
        Commit is { Length: 40 } && Commit.All(Uri.IsHexDigit);

    internal bool HasValidInstaller =>
        HasValidIdentity &&
        IsFileName(InstallerFileName) &&
        string.Equals(InstallerFileName, BuildId + ".exe", StringComparison.OrdinalIgnoreCase) &&
        InstallerSha256 is { Length: 64 } && InstallerSha256.All(Uri.IsHexDigit);

    internal LocalBuildInfo ToInfo() => new(
        BuildId, VersionName, Configuration, CreatedAt, Branch, Commit);

    internal static bool IsFileName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.EndsWith(' ') && !value.EndsWith('.');
}
#endif
