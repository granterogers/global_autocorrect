using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlobalAutocorrect.Windows.Native;
using GlobalAutocorrect.Windows.Write;
using Xunit;
using static GlobalAutocorrect.Windows.Native.NativeMethods;
using Path = System.IO.Path;

namespace GlobalAutocorrect.E2E.Support;

public static class Desk
{
    public static string Repo { get; } = FindRepo();

    /// <summary>From the test binaries, or (when they run from a copy outside the repo) from the working directory.</summary>
    private static string FindRepo()
    {
        // GA_REPO: set when the tests run from a copy (the test runner changes the working directory to its own).
        if (Environment.GetEnvironmentVariable("GA_REPO") is { } repo && File.Exists(Path.Combine(repo, "GlobalAutocorrect.slnx"))) return repo;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = new DirectoryInfo(start);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "GlobalAutocorrect.slnx"))) d = d.Parent;
            if (d is not null) return d.FullName;
        }
        throw new InvalidOperationException("Run the E2E tests from inside the repository.");
    }

    /// <summary>2.8: only take over the keyboard once the user has been away for a while; otherwise skip.</summary>
    public static void RequireIdle(TimeSpan? idle = null, TimeSpan? wait = null)
    {
        var need = idle ?? TimeSpan.FromMinutes(2);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < (wait ?? TimeSpan.FromMinutes(10)))
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            GetLastInputInfo(ref info);
            if (unchecked((uint)Environment.TickCount - info.dwTime) >= need.TotalMilliseconds) return;
            Thread.Sleep(2000);
        }
        Assert.Skip("The user is at the keyboard; E2E tests wait until they've been away for 2 minutes (2.8).");
    }

    /// <summary>Real OS-level typing without our tag, so the app's hook sees it as the user (11.3.c).</summary>
    public static void Type(string text, int msPerKey = 45)
    {
        var typer = new SendInputWriter(0);
        foreach (char c in text)
        {
            typer.Type(c.ToString());
            Thread.Sleep(msPerKey);
        }
    }

    public static void Keys(params ushort[] vks)
    {
        var inputs = new List<INPUT>();
        foreach (var vk in vks) inputs.Add(Key(vk, false));
        foreach (var vk in vks.Reverse()) inputs.Add(Key(vk, true));
        SendInputWriter.Send([.. inputs]);
        Thread.Sleep(150);
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC), dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    public static bool WaitUntil(Func<bool> condition, int ms = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }
}

/// <summary>A scripted stand-in for llama-server on loopback (11.1.d): maps the text it receives to a cleaned text.</summary>
public sealed class MockLlm : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string, string> _clean;
    public string Url { get; }
    public int Requests;

    public MockLlm(Func<string, string> clean)
    {
        _clean = clean;
        int port = Random.Shared.Next(41000, 49000);
        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch (Exception) { return; }
            string reply = "{}";
            if (ctx.Request.Url!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Requests);
                using var r = new StreamReader(ctx.Request.InputStream);
                var body = JsonNode.Parse(await r.ReadToEndAsync())!;
                string user = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
                reply = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = _clean(user) } }) }.ToJsonString();
            }
            byte[] bytes = Encoding.UTF8.GetBytes(reply);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose() => _listener.Close();
}

/// <summary>The real app, started with an isolated data folder and the mock model (GA_E2E=1).</summary>
public sealed class AppUnderTest : IDisposable
{
    private readonly Process _proc;
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "ga-e2e-" + Guid.NewGuid().ToString("N")[..8]);

    public AppUnderTest(MockLlm llm, bool prediction = false)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "settings.json"),
            $$"""{ "FirstRunDone": true, "StartWithWindows": false, "PredictionEnabled": {{(prediction ? "true" : "false")}} }""");
        // GA_E2E_APP: a copy of the build outside the repo, so a rebuild during the run can't hit a locked exe.
        string exe = Environment.GetEnvironmentVariable("GA_E2E_APP") is { } dir
            ? Path.Combine(dir, "GlobalAutocorrect.exe")
            : Path.Combine(Desk.Repo, "src", "GlobalAutocorrect.App", "bin", "Debug", "net10.0-windows", "GlobalAutocorrect.exe");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment["GA_E2E"] = "1";
        psi.Environment["GA_E2E_LLM_URL"] = llm.Url;
        psi.Environment["GA_DATA_DIR"] = DataDir;
        _proc = Process.Start(psi)!;
        Assert.True(Desk.WaitUntil(() => File.Exists(Path.Combine(DataDir, "counters.json")), 20_000), "the app did not start");
    }

    /// <summary>11.3.d: checked before any text assertion.</summary>
    public long Desyncs => Counter("desyncs");
    public long Corrections => Counter("instantCorrections") + Counter("aiCorrections");

    private long Counter(string name)
    {
        Thread.Sleep(1200);   // the app saves counts every second under E2E
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, "counters.json")));
            return doc.RootElement.GetProperty("Counts").TryGetProperty(name, out var v) ? v.GetInt64() : 0;
        }
        catch (IOException) { return -1; }
    }

    public void Dispose()
    {
        try { _proc.Kill(entireProcessTree: true); _proc.WaitForExit(5000); } catch (InvalidOperationException) { }
        try { Directory.Delete(DataDir, true); } catch (IOException) { }
    }
}
