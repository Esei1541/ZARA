using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zara.Application.Updates;
using Zara.Infrastructure.Windows.Updates;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class GitHubReleaseUpdateSourceTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "zara-update-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] InstallerBytes = Encoding.UTF8.GetBytes("verified installer payload");
    private static readonly string InstallerHash = Convert.ToHexString(SHA256.HashData(InstallerBytes));
    private const string InstallerName = "ZARA-1.2.3-win-x64-Setup.exe";
    private const string InstallerUrl = "https://github.com/Esei1541/ZARA/releases/download/v1.2.3/" + InstallerName;

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ParsesPublishedStableReleaseAndSetsPublicGitHubHeaders()
    {
        using var client = Client((request, _) =>
        {
            Assert.AreEqual("https://api.github.com/repos/Esei1541/ZARA/releases/latest", request.RequestUri!.ToString());
            Assert.AreEqual("application/vnd.github+json", request.Headers.Accept.Single().MediaType);
            Assert.AreEqual("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            Assert.IsTrue(request.Headers.UserAgent.ToString().Contains("ZARA-Updater", StringComparison.Ordinal));
            Assert.IsNull(request.Headers.Authorization);
            return Task.FromResult(JsonResponse(ReleaseJson(
                tag: "v1.2.3+build.7",
                url: InstallerUrl.Replace("/v1.2.3/", "/v1.2.3+build.7/", StringComparison.Ordinal))));
        });
        using var source = new GitHubReleaseUpdateSource(client, _directory);

        ReleaseUpdate release = await source.GetLatestAsync();

        Assert.AreEqual(new Version(1, 2, 3), release.Version);
        Assert.AreEqual("release notes", release.Notes);
        Assert.AreEqual(InstallerHash, release.Sha256);
        Assert.AreEqual((long)InstallerBytes.Length, release.Size);
    }

    [TestMethod]
    [DataRow("v1.2.3-beta", false, false)]
    [DataRow("v1.2.3", true, false)]
    [DataRow("v1.2.3", false, true)]
    [DataRow("v01.2.3", false, false)]
    public async Task RejectsNonStableOrMalformedRelease(string tag, bool draft, bool prerelease)
    {
        using var client = Client((_, _) => Task.FromResult(JsonResponse(ReleaseJson(tag, draft, prerelease))));
        using var source = new GitHubReleaseUpdateSource(client, _directory);

        UpdateException exception = await Assert.ThrowsExactlyAsync<UpdateException>(() => source.GetLatestAsync());
        Assert.AreEqual("UPD-GH-INVALID-RESPONSE", exception.ErrorCode);
    }

    [TestMethod]
    public async Task RequiresExactUploadedInstallerWithTrustedUrlAndDigest()
    {
        foreach (string json in new[]
        {
            ReleaseJson(assetName: "another.exe"),
            ReleaseJson(assetState: "new"),
            ReleaseJson(url: "https://example.com/installer.exe"),
            ReleaseJson(digest: "sha256:broken"),
        })
        {
            using var client = Client((_, _) => Task.FromResult(JsonResponse(json)));
            using var source = new GitHubReleaseUpdateSource(client, _directory);
            UpdateException exception = await Assert.ThrowsExactlyAsync<UpdateException>(() => source.GetLatestAsync());
            Assert.IsTrue(exception.ErrorCode is "UPD-GH-ASSET-MISSING" or "UPD-GH-INVALID-RESPONSE");
        }
    }

    [TestMethod]
    public async Task DistinguishesHttpStatusAndRateLimit()
    {
        using var httpClient = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var httpSource = new GitHubReleaseUpdateSource(httpClient, _directory);
        Assert.AreEqual("UPD-GH-HTTP-500",
            (await Assert.ThrowsExactlyAsync<UpdateException>(() => httpSource.GetLatestAsync())).ErrorCode);

        using var rateClient = Client((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        });
        using var rateSource = new GitHubReleaseUpdateSource(rateClient, _directory);
        Assert.AreEqual("UPD-GH-RATE-LIMIT",
            (await Assert.ThrowsExactlyAsync<UpdateException>(() => rateSource.GetLatestAsync())).ErrorCode);
    }

    [TestMethod]
    public async Task DistinguishesKnownOfflineFromOtherConnectionFailure()
    {
        using var client = Client((_, _) => throw new HttpRequestException("network unavailable"));
        using var offline = Source(client, () => false);
        using var connected = Source(client, () => true);

        Assert.AreEqual("UPD-NET-OFFLINE",
            (await Assert.ThrowsExactlyAsync<UpdateException>(() => offline.GetLatestAsync())).ErrorCode);
        Assert.AreEqual("UPD-NET-CONNECT",
            (await Assert.ThrowsExactlyAsync<UpdateException>(() => connected.GetLatestAsync())).ErrorCode);
    }

    [TestMethod]
    public async Task DistinguishesTimeoutFromCallerCancellation()
    {
        using var client = Client(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using var source = Source(client, () => true, TimeSpan.FromMilliseconds(20));
        Assert.AreEqual("UPD-NET-TIMEOUT",
            (await Assert.ThrowsExactlyAsync<UpdateException>(() => source.GetLatestAsync())).ErrorCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.GetLatestAsync(cancellation.Token));
    }

    [TestMethod]
    public async Task DownloadsVerifiesAndReusesCachedInstaller()
    {
        int requests = 0;
        using var client = Client((request, _) =>
        {
            requests++;
            Assert.AreEqual(InstallerUrl, request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(InstallerBytes),
            });
        });
        using var source = new GitHubReleaseUpdateSource(client, _directory);
        ReleaseUpdate release = ValidRelease();
        var progress = new RecordedProgress();

        string path = await source.DownloadAsync(release, progress);
        Assert.AreEqual(Path.Combine(_directory, "1.2.3", InstallerName), path);
        CollectionAssert.AreEqual(InstallerBytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(100, progress.Values.Last());
        Assert.AreEqual(progress.Values.Count, progress.Values.Distinct().Count());
        Assert.AreEqual(path, await source.DownloadAsync(release));
        Assert.AreEqual(1, requests);

        await File.WriteAllTextAsync(path, "damaged cache");
        Assert.AreEqual(path, await source.DownloadAsync(release));
        CollectionAssert.AreEqual(InstallerBytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(2, requests);
    }

    [TestMethod]
    public async Task RejectsWrongLengthAndHashWithoutPublishingExecutable()
    {
        foreach ((byte[] bytes, ReleaseUpdate release, string error) in new[]
        {
            (InstallerBytes[..^1], ValidRelease(), "UPD-DOWNLOAD-SIZE"),
            (InstallerBytes, ValidRelease("A" + InstallerHash[1..]), "UPD-DOWNLOAD-HASH"),
        })
        {
            using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            }));
            using var source = new GitHubReleaseUpdateSource(client, _directory);
            UpdateException exception = await Assert.ThrowsExactlyAsync<UpdateException>(() => source.DownloadAsync(release));
            Assert.AreEqual(error, exception.ErrorCode);
            Assert.IsFalse(File.Exists(Path.Combine(_directory, "1.2.3", InstallerName)));
            Assert.IsEmpty(Directory.GetFiles(Path.Combine(_directory, "1.2.3"), "*.tmp"));
        }
    }

    [TestMethod]
    public async Task RejectsForgedDownloadUrlBeforeHttpRequest()
    {
        using var client = Client((_, _) => throw new AssertFailedException("No request should be sent"));
        using var source = new GitHubReleaseUpdateSource(client, _directory);
        ReleaseUpdate release = ValidRelease() with { InstallerUri = new Uri("https://example.com/installer.exe") };

        Assert.AreEqual("UPD-GH-ASSET-MISSING",
            (await Assert.ThrowsExactlyAsync<UpdateException>(() => source.DownloadAsync(release))).ErrorCode);
    }

    [TestMethod]
    public async Task CanceledStreamRemovesTemporaryFile()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancelAfterFirstReadStream(InstallerBytes, cancellation)),
        }));
        using var source = new GitHubReleaseUpdateSource(client, _directory);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.DownloadAsync(ValidRelease(), cancellationToken: cancellation.Token));
        string versionDirectory = Path.Combine(_directory, "1.2.3");
        Assert.IsFalse(File.Exists(Path.Combine(versionDirectory, InstallerName)));
        Assert.IsEmpty(Directory.GetFiles(versionDirectory, "*.tmp"));
    }

    private GitHubReleaseUpdateSource Source(HttpClient client, Func<bool> networkAvailable, TimeSpan? checkTimeout = null) =>
        new(client, _directory, networkAvailable, checkTimeout ?? TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(15));

    private static ReleaseUpdate ValidRelease(string? hash = null) =>
        new(new Version(1, 2, 3), "release notes", new Uri(InstallerUrl), hash ?? InstallerHash, InstallerBytes.Length);

    private static string ReleaseJson(
        string tag = "v1.2.3", bool draft = false, bool prerelease = false,
        string assetName = InstallerName, string assetState = "uploaded",
        string url = InstallerUrl, string? digest = null) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            body = "release notes",
            draft,
            prerelease,
            assets = new[] { new
            {
                name = assetName,
                state = assetState,
                size = InstallerBytes.Length,
                digest = digest ?? "sha256:" + InstallerHash,
                browser_download_url = url,
            } },
        });

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new FakeHandler(send)) { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class RecordedProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }

    private sealed class CancelAfterFirstReadStream(byte[] content, CancellationTokenSource cancellation) : Stream
    {
        private bool _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_read)
            {
                return ValueTask.FromResult(0);
            }

            _read = true;
            int count = Math.Min(buffer.Length, content.Length);
            content.AsMemory(0, count).CopyTo(buffer);
            cancellation.Cancel();
            return ValueTask.FromResult(count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
