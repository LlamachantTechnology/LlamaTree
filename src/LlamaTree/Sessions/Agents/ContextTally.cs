using System.Collections.Concurrent;
using System.Text.Json;

namespace LlamaTree.Sessions;

/// <summary>
/// A conversation's context as an agent's JSONL file tells it, line by line: the size now, the model's window when the
/// file says, and the compactions. Kept per file, reading only the lines added since last time, as the meter asks
/// every few seconds. Each agent says what its lines mean with a <see cref="Line"/>.
/// </summary>
internal sealed class ContextTally
{
    /// <summary>Reads one parsed line into the tally.</summary>
    public delegate void Line(JsonElement line, ContextTally tally);

    private readonly object gate = new();
    private long offset;

    /// <summary>What the agent holds now (0 while it hasn't said, or just after a compaction it doesn't size).</summary>
    public int Tokens { get; set; }

    /// <summary>The model's window, when the file says (0 otherwise).</summary>
    public int Window { get; set; }

    public int Compactions { get; set; }
    public DateTime? CompactedAt { get; set; }
    public string? Model { get; set; }

    /// <summary>A compaction at <paramref name="at"/>: what the agent holds is unknown until its next reply says.</summary>
    public void Compacted(DateTime? at)
    {
        Compactions++;
        CompactedAt = at;
        Tokens = 0;
    }

    private static readonly ConcurrentDictionary<string, ContextTally> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tally of <paramref name="path"/>, brought up to date; a file rewritten shorter is read again from the start.</summary>
    public static ContextTally Read(string path, Line read)
    {
        var t = Cache.GetOrAdd(path, _ => new ContextTally());
        lock (t.gate)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < t.offset) (t.offset, t.Tokens, t.Window, t.Compactions, t.CompactedAt, t.Model) = (0, 0, 0, 0, null, null);
                stream.Seek(t.offset, SeekOrigin.Begin);
                ConversationReader.ReadLines(stream, ref t.offset, bytes =>
                {
                    if (bytes.Length < 2 || bytes[0] != (byte)'{') return;
                    try
                    {
                        using var doc = JsonDocument.Parse(bytes.ToArray());
                        read(doc.RootElement, t);
                    }
                    catch (Exception ex) when (ConversationReader.IsMalformedLine(ex)) { }
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return t;
        }
    }

    /// <summary>A whole number field of an object; 0 when it's missing or of another type.</summary>
    public static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? (int)Math.Clamp(n, 0, int.MaxValue) : 0;
}
