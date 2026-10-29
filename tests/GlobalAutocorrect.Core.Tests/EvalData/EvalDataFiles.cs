using System.Text.Json;

namespace GlobalAutocorrect.Core.Tests.EvalData;

internal static class EvalDataFiles
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GlobalAutocorrect.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("GlobalAutocorrect.slnx not found above the test binaries.");
    }

    public static string Path_(string name) => Path.Combine(RepoRoot(), "data", "eval", name);

    /// <summary>Reads a JSONL file into one JsonElement per non-empty line.</summary>
    public static List<JsonElement> ReadJsonLines(string name)
    {
        var list = new List<JsonElement>();
        foreach (string line in File.ReadLines(Path_(name)))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            list.Add(doc.RootElement.Clone());
        }
        return list;
    }

    public static string Str(this JsonElement e, string prop) => e.GetProperty(prop).GetString()!;
}
