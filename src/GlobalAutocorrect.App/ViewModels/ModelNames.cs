namespace GlobalAutocorrect.App.ViewModels;

/// <summary>Friendly names for the GGUF files in the model cache (9.3.b).</summary>
public static class ModelNames
{
    public const string Recommended = "gemma-4-E4B-it-Q5_K_M.gguf";

    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemma-4-E4B-it-Q5_K_M.gguf"] = "Gemma 4 E4B (recommended)",
        ["gemma-4-E4B-it-Q4_K_M.gguf"] = "Gemma 4 E4B, smaller download",
        ["gemma-4-E2B-it-Q5_K_M.gguf"] = "Gemma 4 E2B, lighter and faster",
        ["gemma-4-E2B-it-Q4_K_M.gguf"] = "Gemma 4 E2B, smallest",
        ["Qwen3.5-4B-Q5_K_M.gguf"] = "Qwen 3.5 4B",
        ["Qwen3.5-4B-Q4_K_M.gguf"] = "Qwen 3.5 4B, smaller download",
    };

    public static string Friendly(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return "Not chosen";
        if (Known.TryGetValue(file, out var name)) return name;
        string stem = file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? file[..^5] : file;
        return stem.Replace('-', ' ').Replace('_', ' ');
    }
}
