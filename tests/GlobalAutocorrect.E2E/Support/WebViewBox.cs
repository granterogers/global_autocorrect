using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace GlobalAutocorrect.E2E.Support;

/// <summary>
/// D126: a desktop window that hosts a web editor in WebView2, the way new Teams (CKEditor 5 in msedgewebview2), the
/// WhatsApp app and other WebView2 apps do. The field's process is msedgewebview2 while the window in front is this test
/// host: the split that dropped every fix in new Teams as "not-sent" (D123), which no browser test could show.
/// </summary>
internal sealed class WebViewBox : IDisposable
{
    private readonly System.Windows.Window _window;
    private readonly WebView2 _view;

    public WebViewBox(string page)
    {
        System.Windows.Window? window = null;
        WebView2? view = null;
        Exception? failed = null;
        using var ready = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            view = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Path.Combine(Path.GetTempPath(), "ga-e2e-webview2") },
            };
            window = new System.Windows.Window { Title = "GA E2E WebView2 host", Width = 760, Height = 360, Content = view, Topmost = true };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await view.EnsureCoreWebView2Async();
                    view.CoreWebView2.NavigationCompleted += (_, _) => ready.Set();
                    view.CoreWebView2.Navigate(new Uri(Path.Combine(Desk.Repo, "tests", "pages", page + ".html")).AbsoluteUri);
                }
                catch (Exception e) { failed = e; ready.Set(); }
            };
            window.Show();
            System.Windows.Threading.Dispatcher.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        Assert.True(ready.Wait(30_000, TestContext.Current.CancellationToken), "the WebView2 window did not load");
        if (failed is not null) Assert.Skip($"WebView2 is not available here: {failed.GetType().Name} 0x{failed.HResult:X8} {failed.Message}");
        (_window, _view) = (window!, view!);
        Assert.True(Desk.WaitUntil(() => Eval("typeof window.getText === 'function'") == "true", 15_000), "the page did not start");
        nint hwnd = _window.Dispatcher.Invoke(() => new System.Windows.Interop.WindowInteropHelper(_window).Handle);
        Desk.BringToFront(hwnd);
        Desk.ClickCentre(hwnd);   // focus and a session break before typing
        Eval("(document.querySelector('.ck-editor__editable, .ProseMirror, [contenteditable=true]') || document.body).focus(), true");
        Thread.Sleep(1500);   // Chromium turns on full accessibility once UIA connects (10.6.d)
    }

    /// <summary>Runs a script in the page and returns its JSON result.</summary>
    public string Eval(string script) =>
        _view.Dispatcher.Invoke(() => _view.CoreWebView2.ExecuteScriptAsync(script)).GetAwaiter().GetResult();

    /// <summary>The text as the editor shows it (the same script as the browser tests).</summary>
    public string Text => JsonSerializer.Deserialize<string>(Eval($"({RealTypingTests.ShownText})()")) ?? "";

    public void Dispose() => _window.Dispatcher.Invoke(() => { _window.Close(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); });
}
