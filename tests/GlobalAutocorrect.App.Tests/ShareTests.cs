using System.IO;
using System.Net;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.App.Tests.Settings;
using GlobalAutocorrect.App.ViewModels;
using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Learning;
using Xunit;

namespace GlobalAutocorrect.App.Tests;

/// <summary>D179: "Share what it learns": off until switched on, sends what the About page shows, at most once a day.</summary>
public sealed class ShareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GlobalAutocorrect.App.Tests", "share-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private bool _on = true;
    private BrainShare? _target = new("https://relay.example/v1/learning");
    private HttpStatusCode _status = HttpStatusCode.NoContent;
    private readonly List<string> _sent = [];

    public ShareTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private LearningSharer Sharer() => new(
        () => _on, () => _target,
        () => new Dictionary<string, AppStats> { ["notepad"] = new() { Writes = 12, Undos = 1 } },
        () => new Dictionary<string, int> { ["0123456789abcdef"] = 1 },
        _dir, "1.50.0", _ => { },
        (_, json, _) => { _sent.Add(json); return Task.FromResult(_status); },
        () => _now);

    [Fact]
    public async Task R8_6_D179_NothingIsSentUntilSwitchedOnAndARelayIsNamed()
    {
        _on = false;
        var s = Sharer();
        _now = _now.AddHours(1);
        await s.TickAsync(TestContext.Current.CancellationToken);
        _on = true; _target = null;
        await s.TickAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_sent);
        Assert.False(s.Available);
        Assert.False(new GlobalAutocorrect.Core.Settings.AppSettings().ShareLearning);   // off by default
    }

    [Fact]
    public async Task D179_ItWaitsAfterStartAndThenSendsAtMostOnceADay()
    {
        var s = Sharer();
        await s.TickAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_sent);                                                   // start-up isn't slowed
        _now = _now.AddMinutes(Tunables.ShareFirstAfterMinutes.Value);
        await s.TickAsync(TestContext.Current.CancellationToken);
        await s.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(_sent);
        Assert.Equal("1 program and 1 undone change", s.LastSent!.Value.What);
        _now = _now.AddHours(Tunables.ShareEveryHours.Value);
        await s.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _sent.Count);
        Assert.Equal(_now, Sharer().LastSent!.Value.At);                      // remembered across restarts
    }

    [Fact]
    public async Task R8_6_D179_WhatIsSentIsExactlyWhatTheAboutPageShows()
    {
        var s = Sharer();
        s.SendSoon();                                                          // just switched on: no waiting for the first send
        await s.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(File.ReadAllText(s.WritePreview()), Assert.Single(_sent));
        Assert.NotNull(ShareLearning.Parse(_sent[0]));
    }

    [Fact]
    public async Task D179_AFailedSendIsExplainedAndTriedAgain()
    {
        var s = Sharer();
        s.SendSoon();
        _status = HttpStatusCode.TooManyRequests;
        await s.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal("The server is busy. It tries again later.", s.Problem);
        Assert.Null(s.LastSent);
        _now = _now.AddHours(1);
        _status = HttpStatusCode.NoContent;
        await s.TickAsync(TestContext.Current.CancellationToken);
        Assert.Null(s.Problem);
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task D179_AnIdTheRelayKnowsWithAnotherKeyIsReplaced()
    {
        var s = Sharer();
        string first = s.Build().Id;
        s.SendSoon();
        _status = HttpStatusCode.Forbidden;
        await s.TickAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(first, s.Build().Id);
    }

    [Fact]
    public void D179_TheAboutPageSaysWhatSharingIsDoing()
    {
        using var services = new FakeServices();
        var share = new FakeShare();
        var vm = new AboutViewModel(services, Path.Combine(services.Dir, "metrics.json"), Path.Combine(services.Dir, "NOTICE.md"),
            new GlobalAutocorrect.App.Tray.StartupRegistration(@"Software\GlobalAutocorrectTests\Share", @"C:\App\x.exe"), _ => { }, share: share);
        Assert.False(vm.ShareLearning);
        Assert.False(vm.ShareAvailable);
        Assert.Equal("Not open yet: nothing is sent.", vm.ShareStatus);

        share.Available = true;
        vm.Refresh();
        Assert.True(vm.ShareAvailable);
        Assert.Equal("Off: nothing is sent.", vm.ShareStatus);
        vm.ShareLearning = true;
        Assert.True(services.Settings.ShareLearning);
        Assert.True(share.SoonAsked);
        Assert.Equal("On. The first send is in a few minutes.", vm.ShareStatus);
    }

    private sealed class FakeShare : IShareService
    {
        public bool Available { get; set; }
        public (DateTimeOffset At, string What)? LastSent { get; set; }
        public string? Problem { get; set; }
        public bool SoonAsked { get; private set; }
        public string WritePreview() => "";
        public void SendSoon() => SoonAsked = true;
        public event Action? Changed { add { } remove { } }
    }
}
