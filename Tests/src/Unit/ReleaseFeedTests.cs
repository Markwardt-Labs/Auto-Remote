namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ReleaseFeedTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private readonly string release = """
        {
          "tag_name": "1.2.3",
          "assets": [
            { "name": "AutoRemote-linux", "browser_download_url": "https://example.com/a", "size": 5, "digest": "sha256:AB" },
            { "name": "old", "browser_download_url": "https://example.com/b", "size": 7, "digest": null },
            { "name": "broken" }
          ]
        }
        """;

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task Latest_ReadsTheTagAndTheFilesWithTheirChecksums()
    {
        Handler handler = new(_ => Json(release));
        ReleaseFeed feed = new(new HttpClient(handler), "owner/repo");

        ReleaseInfo? latest = await feed.Latest(CancellationToken.None);

        Assert.Equal("1.2.3", latest!.Version.ToString());
        Assert.Equal(["AutoRemote-linux", "old"], latest.Assets.Select(asset => asset.Name));
        Assert.Equal("AB", latest.Assets[0].Sha256);
        Assert.Null(latest.Assets[1].Sha256);
        Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest", handler.Requests[0].RequestUri!.ToString());
        Assert.NotEmpty(handler.Requests[0].Headers.UserAgent);
    }

    [Fact]
    public async Task Latest_ReturnsNothingWhenThereIsNoReleaseOrItsTagIsNotAVersion()
    {
        Assert.Null(await new ReleaseFeed(new HttpClient(new Handler(_ => Json("{}", HttpStatusCode.NotFound))), "o/r").Latest(CancellationToken.None));
        Assert.Null(await new ReleaseFeed(new HttpClient(new Handler(_ => Json("{\"tag_name\":\"nightly\"}"))), "o/r").Latest(CancellationToken.None));
        Assert.Null(await new ReleaseFeed(new HttpClient(new Handler(_ => Json("[]"))), "o/r").Latest(CancellationToken.None));
    }

    [Fact]
    public async Task Latest_FailsWithAnIOExceptionWhenGitHubFails()
    {
        await Assert.ThrowsAsync<IOException>(() => new ReleaseFeed(new HttpClient(new Handler(_ => Json("{}", HttpStatusCode.Forbidden))), "o/r").Latest(CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => new ReleaseFeed(new HttpClient(new Handler(_ => Json("not json"))), "o/r").Latest(CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => new ReleaseFeed(new HttpClient(new Handler(_ => throw new HttpRequestException("offline"))), "o/r").Latest(CancellationToken.None));
    }

    [Fact]
    public async Task Download_WritesTheFileReportsProgressAndChecksTheDigest()
    {
        using TempFolder folder = new();
        byte[] content = [1, 2, 3, 4, 5];
        ReleaseAsset asset = new() { Name = "f", Url = "https://example.com/f", Size = 5, Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() };
        List<double> reported = [];
        ReleaseFeed feed = new(new HttpClient(new Handler(_ => new HttpResponseMessage { Content = new ByteArrayContent(content) })), "o/r");

        await feed.Download(asset, folder.Combine("f"), new Immediate(reported), CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(folder.Combine("f")));
        Assert.Equal(1, reported[^1]);
    }

    [Theory]
    [InlineData(5, "00")]
    [InlineData(6, null)]
    public async Task Download_RejectsAFileThatDoesNotMatchAndLeavesNothing(long size, string? sha)
    {
        using TempFolder folder = new();
        ReleaseAsset asset = new() { Name = "f", Url = "https://example.com/f", Size = size, Sha256 = sha };
        ReleaseFeed feed = new(new HttpClient(new Handler(_ => new HttpResponseMessage { Content = new ByteArrayContent([1, 2, 3, 4, 5]) })), "o/r");

        await Assert.ThrowsAsync<IOException>(() => feed.Download(asset, folder.Combine("f"), null, CancellationToken.None));

        Assert.False(File.Exists(folder.Combine("f")));
    }

    [Fact]
    public async Task Download_FailsWithAnIOExceptionOnAnErrorStatusOrAnOfflineNetwork()
    {
        using TempFolder folder = new();
        ReleaseAsset asset = new() { Name = "f", Url = "https://example.com/f", Size = 5 };

        await Assert.ThrowsAsync<IOException>(() => new ReleaseFeed(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), "o/r").Download(asset, folder.Combine("f"), null, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => new ReleaseFeed(new HttpClient(new Handler(_ => throw new HttpRequestException("offline"))), "o/r").Download(asset, folder.Combine("f"), null, CancellationToken.None));
    }

    [Fact]
    public void Factory_CreatesTheFeedOfTheRepository() => Assert.NotNull(new IReleaseFeed.Factory().Create());

    private sealed class Immediate(List<double> reported) : IProgress<double>
    {
        public void Report(double value) => reported.Add(value);
    }
}
