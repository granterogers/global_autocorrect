using System.IO;
using Path = System.IO.Path;
using GlobalAutocorrect.App.Models;
using Xunit;

namespace GlobalAutocorrect.App.Tests;

/// <summary>D106, user request: "label the llama server so it's clear it's part of the autocorrect app".</summary>
public sealed class LabelledServerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ga-label-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void R5_1_D106_TheModelServerRunsUnderTheAppsNameAndFollowsTheOriginal()
    {
        string original = Path.Combine(_dir, "llama-server.exe");
        File.WriteAllText(original, "v1");
        string run = ModelDownloader.Labelled(original, gpu: false);
        Assert.Equal(Path.Combine(_dir, "GlobalAutocorrect-AI.exe"), run);
        Assert.Equal("v1", File.ReadAllText(run));

        File.WriteAllText(original, "v2, a newer llama.cpp");   // an update replaces the original: the copy follows
        Assert.Equal("v2, a newer llama.cpp", File.ReadAllText(ModelDownloader.Labelled(original, gpu: false)));
    }

    [Fact]
    public void R5_1_D106_WithNoServerYetTheOriginalPathIsKept()
    {
        string missing = Path.Combine(_dir, "nothing", "llama-server.exe");
        Assert.Equal(missing, ModelDownloader.Labelled(missing, gpu: false));
    }
}
