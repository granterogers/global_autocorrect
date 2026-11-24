using System.Net;
using System.Net.Http;
using System.Text;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.App.ViewModels;

namespace GlobalAutocorrect.App;

public partial class App
{
    private LearningSharer? _share;
    private HttpClient? _shareHttp;

    /// <summary>D179: "Share what it learns". Never under the screen tests (GA_E2E); sends nothing until the user switches it on.</summary>
    private void StartSharing()
    {
        if (Environment.GetEnvironmentVariable("GA_E2E") == "1") return;
        _shareHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _shareHttp.DefaultRequestHeaders.UserAgent.ParseAdd("GlobalAutocorrect");
        var http = _shareHttp;
        _share = new LearningSharer(
            () => Services.Settings.ShareLearning,
            () => Host.ShareTarget,
            () => Host.Health.Local,
            () => Host.Mistakes.Local,
            AppHost.DataDir,
            AboutViewModel.InstalledVersion,
            AppHost.Diagnose,
            async (url, json, ct) =>
            {
                using var body = new StringContent(json, Encoding.UTF8, "application/json");
                using var r = await http.PostAsync(url, body, ct).ConfigureAwait(false);
                return r.StatusCode;
            });
        _share.Start();
        Exit += (_, _) =>
        {
            _share.Dispose();
            _shareHttp.Dispose();
        };
    }
}
