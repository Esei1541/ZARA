using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zara.Application.Updates;

namespace Zara.Infrastructure.Windows.Updates;

/// <summary>Reads the public stable GitHub release and caches only verified installers.</summary>
public sealed class GitHubReleaseUpdateSource : IReleaseUpdateSource, IDisposable
{
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/Esei1541/ZARA/releases/latest");
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
    private static readonly Regex StableTag = new(
        @"^v(?<version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
    private static readonly Regex Sha256Hex = new(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _downloadDirectory;
    private readonly Func<bool> _networkAvailable;
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _downloadTimeout;

    public GitHubReleaseUpdateSource()
        : this(new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZARA", "Updates"),
            NetworkInterface.GetIsNetworkAvailable, CheckTimeout, DownloadTimeout, ownsClient: true)
    {
    }

    public GitHubReleaseUpdateSource(HttpClient httpClient, string downloadDirectory)
        : this(httpClient, downloadDirectory, NetworkInterface.GetIsNetworkAvailable,
            CheckTimeout, DownloadTimeout, ownsClient: false)
    {
    }

    internal GitHubReleaseUpdateSource(
        HttpClient httpClient, string downloadDirectory, Func<bool> networkAvailable,
        TimeSpan checkTimeout, TimeSpan downloadTimeout, bool ownsClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _downloadDirectory = Path.GetFullPath(downloadDirectory);
        _networkAvailable = networkAvailable ?? throw new ArgumentNullException(nameof(networkAvailable));
        _checkTimeout = checkTimeout;
        _downloadTimeout = downloadTimeout;
        _ownsClient = ownsClient;
    }

    public async Task<ReleaseUpdate> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_checkTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
            SetGitHubHeaders(request);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            return ParseRelease(document.RootElement);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("UPD-NET-TIMEOUT", exception);
        }
        catch (HttpRequestException exception)
        {
            throw ConnectionFailure(exception);
        }
        catch (JsonException exception)
        {
            throw new UpdateException("UPD-GH-INVALID-RESPONSE", exception);
        }
        catch (IOException exception)
        {
            throw new UpdateException("UPD-NET-CONNECT", exception);
        }
    }

    public async Task<string> DownloadAsync(
        ReleaseUpdate release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        string fileName = InstallerFileName(release.Version);
        if (!IsTrustedInstallerUriForVersion(release.InstallerUri, release.Version, fileName) ||
            release.Size <= 0 || !Sha256Hex.IsMatch(release.Sha256))
        {
            throw new UpdateException("UPD-GH-ASSET-MISSING");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_downloadTimeout);
        string directory = Path.Combine(_downloadDirectory, release.Version.ToString());
        string target = Path.Combine(directory, fileName);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(target))
            {
                if (await VerifyFileAsync(target, release, timeout.Token).ConfigureAwait(false))
                {
                    progress?.Report(100);
                    return target;
                }

                File.Delete(target);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, release.InstallerUri);
            SetGitHubHeaders(request);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != release.Size)
            {
                throw new UpdateException("UPD-DOWNLOAD-SIZE");
            }

            await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920];
                long received = 0;
                int lastProgress = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > release.Size)
                    {
                        throw new UpdateException("UPD-DOWNLOAD-SIZE");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, count);
                    int percent = Math.Min(99, (int)(received * 100.0 / release.Size));
                    if (percent != lastProgress)
                    {
                        progress?.Report(percent);
                        lastProgress = percent;
                    }
                }

                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                if (received != release.Size)
                {
                    throw new UpdateException("UPD-DOWNLOAD-SIZE");
                }

                if (!string.Equals(Convert.ToHexString(hasher.GetHashAndReset()), release.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateException("UPD-DOWNLOAD-HASH");
                }
            }

            timeout.Token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
            progress?.Report(100);
            return target;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("UPD-NET-TIMEOUT", exception);
        }
        catch (HttpRequestException exception)
        {
            throw ConnectionFailure(exception);
        }
        catch (IOException exception)
        {
            throw new UpdateException("UPD-DOWNLOAD-FILE", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UpdateException("UPD-DOWNLOAD-FILE", exception);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { /* Keep the original download failure. */ }
            catch (UnauthorizedAccessException) { /* Keep the original download failure. */ }
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static ReleaseUpdate ParseRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryBoolean(root, "draft", out bool draft) ||
            !TryBoolean(root, "prerelease", out bool prerelease) ||
            !TryString(root, "tag_name", out string tag) ||
            !root.TryGetProperty("assets", out JsonElement assets) ||
            assets.ValueKind != JsonValueKind.Array || draft || prerelease)
        {
            throw new UpdateException("UPD-GH-INVALID-RESPONSE");
        }

        string notes = root.TryGetProperty("body", out JsonElement body) && body.ValueKind == JsonValueKind.String
            ? body.GetString() ?? string.Empty
            : string.Empty;
        Match match = StableTag.Match(tag);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out Version? version) ||
            version.Build < 0 || version.Revision >= 0)
        {
            throw new UpdateException("UPD-GH-INVALID-RESPONSE");
        }

        string name = InstallerFileName(version);
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !TryString(asset, "name", out string assetName) ||
                !string.Equals(assetName, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryString(asset, "state", out string state) || state != "uploaded" ||
                !TryInt64(asset, "size", out long size) || size <= 0 ||
                !TryString(asset, "digest", out string digest) ||
                !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                !Sha256Hex.IsMatch(digest[7..]) ||
                !TryString(asset, "browser_download_url", out string url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                !IsTrustedInstallerUri(uri, tag, name))
            {
                throw new UpdateException("UPD-GH-INVALID-RESPONSE");
            }

            return new ReleaseUpdate(version, notes, uri, digest[7..].ToUpperInvariant(), size);
        }

        throw new UpdateException("UPD-GH-ASSET-MISSING");
    }

    private static bool IsTrustedInstallerUri(Uri? uri, string tag, string fileName) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        string.Equals(uri.AbsolutePath,
            "/Esei1541/ZARA/releases/download/" + tag + "/" + fileName, StringComparison.Ordinal);

    private static bool IsTrustedInstallerUriForVersion(Uri? uri, Version version, string fileName)
    {
        if (uri is not { IsAbsoluteUri: true })
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Split('/');
        if (segments.Length != 7 || !StableTag.IsMatch(segments[5]) ||
            !Version.TryParse(StableTag.Match(segments[5]).Groups["version"].Value, out Version? tagVersion) ||
            tagVersion != version)
        {
            return false;
        }

        return IsTrustedInstallerUri(uri, segments[5], fileName);
    }

    private static string InstallerFileName(Version version)
    {
        if (version.Build < 0 || version.Revision >= 0 || version.Major < 0 || version.Minor < 0)
        {
            throw new UpdateException("UPD-GH-INVALID-RESPONSE");
        }

        return $"ZARA-{version}-win-x64-Setup.exe";
    }

    private static bool TryString(JsonElement item, string name, out string value)
    {
        value = string.Empty;
        if (!item.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryBoolean(JsonElement item, string name, out bool value)
    {
        value = false;
        if (!item.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryInt64(JsonElement item, string name, out long value)
    {
        value = 0;
        return item.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }

    private static async Task<bool> VerifyFileAsync(string path, ReleaseUpdate release, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != release.Size)
        {
            return false;
        }

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), release.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetGitHubHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("ZARA-Updater/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            (response.StatusCode == HttpStatusCode.Forbidden &&
                response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? values) &&
                values.Contains("0", StringComparer.Ordinal)))
        {
            throw new UpdateException("UPD-GH-RATE-LIMIT");
        }

        throw new UpdateException($"UPD-GH-HTTP-{(int)response.StatusCode}");
    }

    private UpdateException ConnectionFailure(HttpRequestException exception)
    {
        bool offline;
        try { offline = !_networkAvailable(); }
        catch { offline = false; }
        return new UpdateException(offline ? "UPD-NET-OFFLINE" : "UPD-NET-CONNECT", exception);
    }
}
