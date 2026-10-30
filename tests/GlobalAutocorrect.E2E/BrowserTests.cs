using System.IO;
using Path = System.IO.Path;
using GlobalAutocorrect.E2E.Support;
using Microsoft.Playwright;
using Xunit;

namespace GlobalAutocorrect.E2E;

/// <summary>
/// Feature A in browser editors (11.3.b). Playwright only opens the local pages in headed Edge (and Chrome) and reads results back
/// from the editor state; all typing is real SendInput, because Playwright's keyboard never reaches the hook (11.3.c).
/// </summary>
[Trait("Category", "E2E")]
[Collection("screen")]
public sealed class BrowserTests
{
    private static string Model(string s) => s.Replace("i went their", "I went there");

    /// <summary>Opens a local test page in headed Edge (channel "msedge") or Google Chrome (channel "chrome").</summary>
    internal static async Task<(IBrowser Browser, IPage Page, IPlaywright Pw)> Open(string page, string channel = "msedge")
    {
        var pw = await Playwright.CreateAsync();
        string[] args = channel == "msedge"
            ? ["--no-first-run", "--disable-features=msEdgeSidebarV2"]
            : ["--no-first-run", "--no-default-browser-check", "--disable-search-engine-choice-screen"];
        var browser = await pw.Chromium.LaunchAsync(new() { Channel = channel, Headless = false, Args = args });
        var p = await browser.NewPageAsync();
        await p.GotoAsync(new Uri(Path.Combine(Desk.Repo, "tests", "pages", page + ".html")).AbsoluteUri);
        await p.WaitForFunctionAsync("typeof window.getText === 'function'");   // CKEditor 5 starts asynchronously
        await p.BringToFrontAsync();
        // Click the element that actually takes typing: ProseMirror, Quill and Slate render their own editable inside
        // #ed, CKEditor 5 replaces #ed with its own editor UI, and TinyMCE edits inside an iframe it puts after #ed.
        string target = page switch
        {
            "prosemirror" => ".ProseMirror",
            "ckeditor" => ".ck-editor__editable",
            "quill" => ".ql-editor",
            "tinymce" => "#ed_ifr",
            "slate" => "[data-slate-editor]",
            _ => "#ed",
        };
        await p.ClickAsync(target);   // a click is a session break, before any typing
        await Task.Delay(1500);      // Chromium turns on full accessibility once UIA connects (10.6.d)
        return (browser, p, pw);
    }

    private static async Task<string> Text(IPage p) => (await p.EvaluateAsync<string>("window.getText()")).Replace('\u00a0', ' ').TrimEnd('\n');

    [Theory]
    [InlineData("textarea")]
    [InlineData("contenteditable")]
    [InlineData("prosemirror")]
    [InlineData("lexical")]
    [InlineData("ckeditor")]   // CKEditor 5: Microsoft Teams' message box
    [InlineData("quill")]
    [InlineData("tinymce")]    // self-hosted from npm, classic (iframe) mode
    [InlineData("slate")]      // React
    public Task R11_3_b_InstantAndAiFixesInBrowserEditors(string page) => FixesIn(page, "msedge");

    /// <summary>The same editors in Google Chrome (11.3.b): Chrome's own build and accessibility tree, not Edge's.</summary>
    [Theory]
    [InlineData("textarea")]
    [InlineData("contenteditable")]
    [InlineData("prosemirror")]
    [InlineData("lexical")]
    [InlineData("ckeditor")]
    [InlineData("quill")]
    [InlineData("tinymce")]
    [InlineData("slate")]
    public Task R11_3_b_InstantAndAiFixesInBrowserEditors_Chrome(string page) => FixesIn(page, "chrome");

    private static async Task FixesIn(string page, string channel)
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm);
        var (browser, p, pw) = await Open(page, channel);
        try
        {
            Desk.Type("i went their with teh dog. ");
            string got = "";
            for (int i = 0; i < 40 && got != "I went there with the dog."; i++)
            {
                await Task.Delay(150, TestContext.Current.CancellationToken);
                got = (await Text(p)).TrimEnd();
            }
            Assert.True(got == "I went there with the dog.", $"{channel}: got \"{got}\"; {app.Diagnostics()}");
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
