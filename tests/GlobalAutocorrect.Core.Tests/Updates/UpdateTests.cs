using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using GlobalAutocorrect.Core.Updates;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Updates;

/// <summary>D58: the update check against GitHub releases, with the HTTP layer faked.</summary>
public sealed class UpdateTests : IDisposable
{
    private const string Base = "https://github.com/granterogers/global_autocorrect/releases/download/";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ga-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static ReleaseVersion V(string s) => ReleaseVersion.Parse(s);

    /// <summary>Answers each URL from a table; anything else is a 404. Records what was asked for.</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Routes.TryGetValue(request.RequestUri!.ToString(), out var r) ? r() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public void Bytes(string url, byte[] body) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        public void Text(string url, string body) => Bytes(url, Encoding.UTF8.GetBytes(body));
    }

    private static string Json(params (string Tag, bool Pre, string[] Assets)[] releases) =>
        "[" + string.Join(",", releases.Select(r =>
            $$"""{"tag_name":"{{r.Tag}}","prerelease":{{(r.Pre ? "true" : "false")}},"draft":false,"assets":[{{string.Join(",", r.Assets.Select(a =>
                $$"""{"name":"{{a[(a.LastIndexOf('/') + 1)..]}}","browser_download_url":"{{a}}","size":10}"""))}}]}""")) + "]";

    private static string[] Assets(string tag) =>
        [$"{Base}{tag}/GlobalAutocorrect-{tag.TrimStart('v')}.zip", $"{Base}{tag}/SHA256SUMS.txt"];

    // ---- versions ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.9.0-rc1", "0.9.0-rc2")]
    [InlineData("0.9.0-rc2", "0.9.0")]
    [InlineData("0.9.0-rc2", "0.9.0-rc10")]
    [InlineData("0.9.0", "0.9.1-rc1")]
    [InlineData("0.9.1", "0.10.0")]
    [InlineData("0.10.0", "1.0.0-rc1")]
    [InlineData("v0.9.0-rc1", "v0.9.0")]
    [InlineData("0.9.0-alpha", "0.9.0-beta")]
    [InlineData("0.9.0-rc.1", "0.9.0-rc.2")]
    public void R13_3_VersionsCompareAsReleasesWithPreReleaseSuffixes_D58(string older, string newer)
    {
        Assert.True(V(older) < V(newer), $"{older} < {newer}");
        Assert.True(V(newer) > V(older));
        Assert.Equal(-1, V(older).CompareTo(V(newer)));
    }

    [Theory]
    [InlineData("0.9.0-rc1", "v0.9.0-rc1")]
    [InlineData("0.9.0-rc1", "0.9.0-RC1")]
    [InlineData("0.9.0-rc1+abc1234", "0.9.0-rc1")]
    public void R13_3_TheSameVersionWrittenDifferentlyIsEqual_D58(string a, string b)
    {
        Assert.Equal(0, V(a).CompareTo(V(b)));
        Assert.Equal(V(a), V(b));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.9")]
    [InlineData("0.9.0.1")]
    [InlineData("0.9.x")]
    [InlineData("0.9.0-")]
    [InlineData("0.9.0-rc..1")]
    [InlineData("latest")]
    [InlineData("v-1.0.0")]
    public void R13_3_SomethingThatIsntAVersionIsRejected_D58(string text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    // ---- which release -----------------------------------------------------------------------------------------------

    [Fact]
    public void R13_3_PreReleasesAreOfferedOnlyToSomeoneRunningAPreRelease_D58()
    {
        var releases = Releases.Parse(Json(("v0.9.0-rc2", true, Assets("v0.9.0-rc2")), ("v0.9.0", false, Assets("v0.9.0")), ("v0.9.1-rc1", true, Assets("v0.9.1-rc1"))));

        Assert.Equal("0.9.1-rc1", Releases.Choose(releases, V("0.9.0-rc1"))?.Version.ToString());   // pre-release installed: newest of all
        Assert.Null(Releases.Choose(releases, V("0.9.0")));                                          // release installed: rc not offered
        Assert.Equal("0.9.0", Releases.Choose(releases, V("0.8.0"))?.Version.ToString());            // only the release
        // A tag with a suffix counts as a pre-release even if the release isn't marked as one.
        var unmarked = Releases.Parse(Json(("v0.9.1-rc1", false, Assets("v0.9.1-rc1"))));
        Assert.Null(Releases.Choose(unmarked, V("0.9.0")));
    }

    [Theory]
    [InlineData("0.9.0-rc1", "v0.9.0-rc1")]   // the developer's own deploys carry the release's version
    [InlineData("0.9.0-rc2", "v0.9.0-rc1")]
    [InlineData("0.9.0", "v0.9.0")]
    [InlineData("0.9.0", "v0.9.0-rc2")]
    [InlineData("1.0.0", "v0.9.0")]
    public void R13_3_AnEqualOrOlderVersionIsNeverOffered_D58(string installed, string tag)
    {
        var releases = Releases.Parse(Json((tag, tag.Contains('-'), Assets(tag))));
        Assert.Single(releases);
        Assert.Null(Releases.Choose(releases, V(installed)));
    }

    [Fact]
    public void R13_1_OnlyTheAppsOwnReleaseAssetsAreUsed_D58()
    {
        Assert.True(Releases.IsOurAssetUrl(Base + "v0.9.0/GlobalAutocorrect-0.9.0.zip"));
        Assert.False(Releases.IsOurAssetUrl("http://github.com/granterogers/global_autocorrect/releases/download/v0.9.0/GlobalAutocorrect-0.9.0.zip"));
        Assert.False(Releases.IsOurAssetUrl("https://github.com/someone/global_autocorrect/releases/download/v0.9.0/GlobalAutocorrect-0.9.0.zip"));
        Assert.False(Releases.IsOurAssetUrl("https://github.com.evil.example/granterogers/global_autocorrect/releases/download/v0.9.0/x.zip"));
        Assert.False(Releases.IsOurAssetUrl("https://evil.example/granterogers/global_autocorrect/releases/download/v0.9.0/x.zip"));
        Assert.False(Releases.IsOurAssetUrl("https://github.com:8443/granterogers/global_autocorrect/releases/download/v0.9.0/x.zip"));
        Assert.False(Releases.IsOurAssetUrl("https://user@github.com/granterogers/global_autocorrect/releases/download/v0.9.0/x.zip"));

        string evil = "https://evil.example/granterogers/global_autocorrect/releases/download/v0.9.1/GlobalAutocorrect-0.9.1.zip";
        var releases = Releases.Parse(Json(("v0.9.1", false, [evil, Base + "v0.9.1/SHA256SUMS.txt"])));
        Assert.Null(Releases.Choose(releases, V("0.9.0")));
    }

    [Fact]
    public void R13_3_DraftsAndReleasesWithoutTheZipAreSkipped_D58()
    {
        var json = $$"""
            [{"tag_name":"v0.9.2","prerelease":false,"draft":true,"assets":[{"name":"GlobalAutocorrect-0.9.2.zip","browser_download_url":"{{Base}}v0.9.2/GlobalAutocorrect-0.9.2.zip"}]},
             {"tag_name":"v0.9.1","prerelease":false,"draft":false,"assets":[]},
             {"tag_name":"not-a-version","assets":[]},
             {"tag_name":"v0.9.0","prerelease":false,"draft":false,"assets":[{"name":"GlobalAutocorrect-0.9.0.zip","browser_download_url":"{{Base}}v0.9.0/GlobalAutocorrect-0.9.0.zip"}]}]
            """;
        var u = Releases.Choose(Releases.Parse(json), V("0.8.0"));
        Assert.Equal("0.9.0", u?.Version.ToString());
        Assert.Null(u!.Sums);   // offered; installing it is refused (no checksum file)
        Assert.Empty(Releases.Parse("{\"message\":\"API rate limit exceeded\"}"));
        Assert.Empty(Releases.Parse("not json"));
    }

    // ---- the check over HTTP -----------------------------------------------------------------------------------------

    [Fact]
    public async Task R13_3_TheCheckAsksGitHubOnceAndFindsTheNewerRelease_D58()
    {
        var http = new FakeHttp();
        http.Text(Releases.ApiUrl.ToString(), Json(("v0.9.0-rc2", true, Assets("v0.9.0-rc2")), ("v0.9.0-rc1", true, Assets("v0.9.0-rc1"))));
        var client = new UpdateClient(new HttpClient(http));

        var u = await client.CheckAsync(V("0.9.0-rc1"), TestContext.Current.CancellationToken);
        Assert.Equal("0.9.0-rc2", u?.Version.ToString());
        var req = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://api.github.com/repos/granterogers/global_autocorrect/releases?per_page=20", req.RequestUri!.ToString());
        Assert.Null(req.Headers.Authorization);
        Assert.NotEmpty(req.Headers.UserAgent);
        Assert.Null(await client.CheckAsync(V("0.9.0-rc2"), TestContext.Current.CancellationToken));   // equal: nothing
    }

    [Fact]
    public async Task R13_3_ANetworkFailureMeansNoUpdateAndNoError_D58()
    {
        var http = new FakeHttp();
        var client = new UpdateClient(new HttpClient(http)) { CheckTimeout = TimeSpan.FromMilliseconds(200) };
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await client.CheckAsync(V("0.9.0-rc1"), ct));                                   // 404
        http.Routes[Releases.ApiUrl.ToString()] = () => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"rate limit\"}") };
        Assert.Null(await client.CheckAsync(V("0.9.0-rc1"), ct));                                   // rate limited
        http.Routes[Releases.ApiUrl.ToString()] = () => throw new HttpRequestException("offline");
        Assert.Null(await client.CheckAsync(V("0.9.0-rc1"), ct));                                   // offline
        http.Text(Releases.ApiUrl.ToString(), "<html>captive portal</html>");
        Assert.Null(await client.CheckAsync(V("0.9.0-rc1"), ct));                                   // not JSON

        var slow = new UpdateClient(new HttpClient(new SlowHttp())) { CheckTimeout = TimeSpan.FromMilliseconds(100) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await slow.CheckAsync(V("0.9.0-rc1"), ct));                                     // gives up quickly
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    private sealed class SlowHttp : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // ---- download and checksum ---------------------------------------------------------------------------------------

    private static byte[] Zip(bool withInstaller = true)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (withInstaller) using (var w = new StreamWriter(z.CreateEntry("install.ps1").Open())) w.Write("Write-Host 'install'");
            using (var w = new StreamWriter(z.CreateEntry("app/GlobalAutocorrect.exe").Open())) w.Write("exe");
        }
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private (UpdateClient Client, FakeHttp Http, AvailableUpdate Update) Offered(byte[] zip, string? sums)
    {
        var http = new FakeHttp();
        var assets = new List<string> { Base + "v0.9.0-rc2/GlobalAutocorrect-0.9.0-rc2.zip" };
        http.Bytes(assets[0], zip);
        if (sums is not null)
        {
            assets.Add(Base + "v0.9.0-rc2/SHA256SUMS.txt");
            http.Text(assets[1], sums);
        }
        var update = Releases.Choose(Releases.Parse(Json(("v0.9.0-rc2", true, assets.ToArray()))), V("0.9.0-rc1"))!;
        return (new UpdateClient(new HttpClient(http)), http, update);
    }

    [Fact]
    public async Task R13_1_AMatchingDownloadIsUnpackedWithItsInstaller_D58()
    {
        var zip = Zip();
        var (client, _, update) = Offered(zip, $"{Sha(zip)}  GlobalAutocorrect-0.9.0-rc2.zip\n");
        string folder = await client.DownloadAsync(update, Path.Combine(_dir, "work"), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(folder, "install.ps1")));
        Assert.True(File.Exists(Path.Combine(folder, "app", "GlobalAutocorrect.exe")));
        Assert.StartsWith(_dir, folder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R13_1_AChecksumMismatchIsRefusedAndTheDownloadDeleted_D58()
    {
        var zip = Zip();
        var (client, _, update) = Offered(zip, $"{Sha([1, 2, 3])}  GlobalAutocorrect-0.9.0-rc2.zip\n");
        var e = await Assert.ThrowsAsync<UpdateRefusedException>(() => client.DownloadAsync(update, Path.Combine(_dir, "work"), TestContext.Current.CancellationToken));
        Assert.Contains("doesn't match", e.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "work"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(null, "no checksum file")]                                                       // no SHA256SUMS.txt in the release
    [InlineData("", "doesn't list")]                                                             // empty
    [InlineData("0000  GlobalAutocorrect-0.9.0-rc2.zip\n", "doesn't list")]                     // not a SHA-256
    [InlineData("SUMOF  OtherFile.zip\n", "doesn't list")]                                       // another file only
    public async Task R13_1_AMissingOrUnusableChecksumFileIsRefused_D58(string? sums, string reason)
    {
        var zip = Zip();
        sums = sums?.Replace("SUMOF", Sha(zip));
        var (client, http, update) = Offered(zip, sums);
        var e = await Assert.ThrowsAsync<UpdateRefusedException>(() => client.DownloadAsync(update, Path.Combine(_dir, "work"), TestContext.Current.CancellationToken));
        Assert.Contains(reason, e.Message);
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.ToString().EndsWith(".zip", StringComparison.Ordinal));   // the zip isn't even fetched
    }

    [Fact]
    public async Task R13_1_AZipWithoutAnInstallerIsRefused_D58()
    {
        var zip = Zip(withInstaller: false);
        var (client, _, update) = Offered(zip, $"{Sha(zip).ToUpperInvariant()} *GlobalAutocorrect-0.9.0-rc2.zip\r\n");   // sha256sum binary form, CRLF
        var e = await Assert.ThrowsAsync<UpdateRefusedException>(() => client.DownloadAsync(update, Path.Combine(_dir, "work"), TestContext.Current.CancellationToken));
        Assert.Contains("no installer", e.Message);
    }

    [Fact]
    public async Task R13_1_AnAssetFromAnotherSiteIsNeverDownloaded_D58()
    {
        var http = new FakeHttp();
        var client = new UpdateClient(new HttpClient(http));
        var update = new AvailableUpdate(V("0.9.0-rc2"), "v0.9.0-rc2",
            new ReleaseAsset("GlobalAutocorrect-0.9.0-rc2.zip", "https://evil.example/GlobalAutocorrect-0.9.0-rc2.zip", 10),
            new ReleaseAsset("SHA256SUMS.txt", Base + "v0.9.0-rc2/SHA256SUMS.txt", 10));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => client.DownloadAsync(update, Path.Combine(_dir, "work"), TestContext.Current.CancellationToken));
        Assert.Empty(http.Requests);
    }
}
