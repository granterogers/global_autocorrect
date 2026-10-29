using System.IO;
using GlobalAutocorrect.App.Models;

namespace GlobalAutocorrect.App.Services;

/// <summary>The first-run window's view of the model download (9.4). The real one wraps <see cref="ModelDownloader"/>.</summary>
public interface IModelInstaller
{
    /// <summary>True when the AI engine and the given model file are already in the cache.</summary>
    bool IsInstalled(string modelFile);

    Task InstallAsync(IProgress<DownloadProgress> progress, CancellationToken cancellationToken);
}

public sealed class ModelInstaller(ModelDownloader downloader, string modelKey = ModelInstaller.DefaultModelKey) : IModelInstaller
{
    /// <summary>The manifest key of the default model (Gemma 4 E4B, Q5).</summary>
    public const string DefaultModelKey = "gemma4-e4b-q5";

    public bool IsInstalled(string modelFile)
    {
        try { return downloader.IsInstalled(modelFile); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or KeyNotFoundException or UnauthorizedAccessException) { return false; }
    }

    public Task InstallAsync(IProgress<DownloadProgress> progress, CancellationToken cancellationToken) =>
        downloader.InstallAsync(modelKey, progress, cancellationToken);
}
