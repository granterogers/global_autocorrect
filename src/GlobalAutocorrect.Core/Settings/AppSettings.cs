using System.Text.Json;
using System.Text.Json.Serialization;
using GlobalAutocorrect.Core.Engine;

namespace GlobalAutocorrect.Core.Settings;

/// <summary>9.3.b "how bold corrections are": how different a corrected word may be from what was typed (4.5.a).</summary>
public enum Boldness { Cautious, Standard, Bold }

/// <summary>
/// Everything the user can change (9.3). Stored as plain UTF-8 JSON in %APPDATA% (8.6). Never contains typed text.
/// </summary>
public sealed record AppSettings
{
    public bool Paused { get; init; }
    public int PauseMs { get; init; } = 700;                       // 4.4.a, 300–2000
    public Boldness Boldness { get; init; } = Boldness.Standard;   // 0.2.c: fix everything it is confident about
    public bool UnderlineEnabled { get; init; } = true;            // 4.7
    public string Model { get; init; } = "gemma-4-E4B-it-Q5_K_M.gguf";   // 5.2, chosen by Spike A
    public string FallbackModel { get; init; } = "gemma-4-E2B-it-Q5_K_M.gguf";
    public string? PromptFile { get; init; }                         // 5.2: null = the built-in prompt
    public bool PredictionEnabled { get; init; } = true;           // 7.7
    public string PauseShortcut { get; init; } = "Ctrl+Alt+P";     // 8.2
    public string UndoShortcut { get; init; } = "Ctrl+Alt+Z";      // 4.8.b
    public List<string>? ExcludedApps { get; init; }               // 8.4; null = the built-in defaults
    public bool StartWithWindows { get; init; } = true;            // 8.1
    public int IdleUnloadMinutes { get; init; } = 15;              // 5.6.b
    public bool AutoLearnWords { get; init; } = true;              // 8.5
    public bool FirstRunDone { get; init; }

    public EngineOptions ToEngineOptions(EngineOptions tuned) => tuned with
    {
        PauseMs = Math.Clamp(PauseMs, 300, 2000),
        FixWholeParagraph = Boldness != Boldness.Cautious && tuned.FixWholeParagraph,   // D43
        Rules = tuned.Rules with
        {
            MaxNormalisedDistance = Boldness switch
            {
                Boldness.Cautious => Math.Min(tuned.Rules.MaxNormalisedDistance, 0.34),
                Boldness.Bold => Math.Max(tuned.Rules.MaxNormalisedDistance, 0.6),
                _ => tuned.Rules.MaxNormalisedDistance,
            },
            AcceptLikelyKeyboardSlips = Boldness != Boldness.Cautious && tuned.Rules.AcceptLikelyKeyboardSlips,
            LenientWithNonWords = Boldness != Boldness.Cautious && tuned.Rules.LenientWithNonWords,
        },
    };
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlobalAutocorrect", "settings.json");

    public string Path => path;

    /// <summary>Missing or unreadable settings fall back to defaults; a broken file never stops the app.</summary>
    public AppSettings Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(AppSettings s)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json), new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
