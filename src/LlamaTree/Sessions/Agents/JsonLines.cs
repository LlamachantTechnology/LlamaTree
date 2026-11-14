using System.Text.Json;

namespace LlamaTree.Sessions;

/// <summary>Small helpers for reading the JSON lines agents write, shared by their conversation readers.</summary>
internal static class JsonLines
{
    /// <summary>One line parsed as a JSON object; false for anything else (a blank, a half-written line).</summary>
    public static bool TryParse(string line, out JsonElement element)
    {
        element = default;
        line = line.Trim();
        if (line.Length < 2 || line[0] != '{') return false;
        try
        {
            element = JsonDocument.Parse(line).RootElement;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The string field <paramref name="name"/> of an object; null when it's missing or of another type.</summary>
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The text on one line, cut to <paramref name="max"/> characters with an ellipsis.</summary>
    public static string? Truncate(string? s, int max)
    {
        if (s == null) return null;
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
