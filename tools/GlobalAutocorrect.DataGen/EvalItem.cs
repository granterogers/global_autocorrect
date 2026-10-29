using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlobalAutocorrect.DataGen;

/// <summary>One evaluation item as written to noisy.jsonl, holdout.jsonl and tuning.jsonl.</summary>
public sealed record EvalItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("clean")] string Clean,
    [property: JsonPropertyName("noisy")] string Noisy,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("ops")] IReadOnlyList<string> Ops);

internal sealed record CleanMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("clean")] string Clean);

internal sealed record GarbledMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("clean")] string Clean,
    [property: JsonPropertyName("noisy")] string Noisy);

internal static class JsonLines
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static List<T> Read<T>(string path)
    {
        var list = new List<T>();
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            list.Add(JsonSerializer.Deserialize<T>(line, Options) ?? throw new InvalidDataException($"Bad line in {path}"));
        }
        return list;
    }

    /// <summary>UTF-8 without BOM, one JSON object per line, LF line endings.</summary>
    public static void Write<T>(string path, IEnumerable<T> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items) sb.Append(JsonSerializer.Serialize(item, Options)).Append('\n');
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
