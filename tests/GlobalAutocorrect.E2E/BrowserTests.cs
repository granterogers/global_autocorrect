using System.IO;
using Path = System.IO.Path;
using GlobalAutocorrect.E2E.Support;
using Microsoft.Playwright;
using Xunit;

namespace GlobalAutocorrect.E2E;

/// <summary>
/// Feature A in browser editors (11.3.b). Playwright only opens the local pages in headed Edge and reads results back
/// from the editor state; all typing is real SendInput, because Playwright's keyboard never reaches the hook (11.3.c).
/// </summary>
[Trait("Category", "E2E")]
[Collection("screen")]
public sealed class BrowserTests
{
    private static string Model(string s) => s.Replace("i went their", "I went there");

    private static async Task<(IBrowser Browser, IPage Page, IPlaywright Pw)> Open(string page)
    {
        var pw = await Playwright.CreateAsync();
        var browser = await pw.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = false, Args = ["--no-first-run", "--disable-features=msEdgeSidebarV2"] });
        var p = await browser.NewPageAsync();
        await p.GotoAsync(new Uri(Path.Combine(Desk.Repo, "tests", "pages", page + ".html")).AbsoluteUri);
        await p.BringToFrontAsync();
        // Click the element that actually takes typing: ProseMirror renders its own editable inside #ed.
        await p.ClickAsync(page == "prosemirror" ? ".ProseMirror" : "#ed");   // a click is a session break, before any typing
        await Task.Delay(1500);      // Chromium turns on full accessibility once UIA connects (10.6.d)
        return (browser, p, pw);
    }

    private static async Task<string> Text(IPage p) => (await p.EvaluateAsync<string>("window.getText()")).Replace('\u00a0', ' ').TrimEnd('\n');

    [Theory]
    [InlineData("textarea")]
    [InlineData("contenteditable")]
    [InlineData("prosemirror")]
    [InlineData("lexical")]
    public async Task R11_3_b_InstantAndAiFixesInBrowserEditors(string page)
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm);
        var (browser, p, pw) = await Open(page);
        try
        {
            Desk.Type("i went their with teh dog. ");
            string got = "";
            for (int i = 0; i < 40 && got != "I went there with the dog."; i++)
            {
                await Task.Delay(150, TestContext.Current.CancellationToken);
                got = (await Text(p)).TrimEnd();
            }
            Assert.True(got == "I went there with the dog.", $"got \"{got}\"; {app.Diagnostics()}");
            Assert.Equal(0, app.Desyncs);   // 11.3.d (checked first in spirit: a desync would also fail the line above)
        }
        finally
        {
            await browser.CloseAsync();
            pw.Dispose();
        }
    }

    [Fact]
    public async Task R8_3_PasswordFieldsAreNeverTouched()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm);
        var (browser, p, pw) = await Open("password");
        try
        {
            Desk.Type("teh secret ");
            await Task.Delay(2500, TestContext.Current.CancellationToken);
            Assert.Equal("teh secret ", await p.EvaluateAsync<string>("window.getText()"));
            Assert.Equal(0, app.Corrections);
            Assert.Equal(0, llm.Requests);
        }
        finally
        {
            await browser.CloseAsync();
            pw.Dispose();
        }
    }
}
