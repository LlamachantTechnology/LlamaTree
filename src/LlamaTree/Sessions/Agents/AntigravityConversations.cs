using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaTree.Sessions;

/// <summary>
/// Reads agy's saved conversations from ~/.gemini/antigravity-cli without its databases: each conversation's transcript
/// (brain/&lt;id&gt;/.system_generated/logs/transcript.jsonl, one step per line), its title (annotations/&lt;id&gt;.pbtxt),
/// and the prompt history (history.jsonl), which is the only place that says which folder a conversation was had in.
/// Each prompt there names its folder and, except the first of a new conversation, the conversation it went to; that
/// first one is matched to its conversation by its text and time. A conversation agy -p ran has no prompt there, so it
/// isn't listed. agy can't be told which conversation id to use, so a session learns it from these (see <see cref="Live"/>).
/// </summary>
internal static partial class AntigravityConversations
{
    /// <summary>How far into a transcript to look for the first prompt (it's normally the first line).</summary>
    private const int HeadBytes = 256 * 1024;

    /// <summary>How far apart a new conversation's first prompt in history.jsonl and in its transcript can be (the transcript's time is whole seconds).</summary>
    private static readonly TimeSpan FirstPromptSlack = TimeSpan.FromSeconds(5);

    /// <summary>agy's folder: ~/.gemini/antigravity-cli (tests point it elsewhere).</summary>
    internal static string Home { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");

    private static string BrainRoot => Path.Combine(Home, "brain");

    /// <summary>A transcript's first prompt and when it was sent (null while agy hasn't written one).</summary>
    private sealed record Meta(DateTime? Started, string? FirstPrompt);

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, Meta Meta)> Metas = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, string? Title)> Titles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>agy's saved conversations, latest first; only those had in <paramref name="directory"/> when one is given.</summary>
    public static List<ConversationInfo> List(string? directory, int max)
    {
        var dir = CodexConversations.NormalizeDir(directory);
        var history = History();
        var result = new List<ConversationInfo>();
        foreach (var (id, file) in Transcripts().OrderByDescending(t => t.File.LastWriteTimeUtc))
        {
            var meta = Read(file);
            if (history.Workspace(id, meta) is not { } workspace) continue;   // agy -p, or not had in any folder
            if (dir != null && !CodexConversations.SameDir(workspace, dir)) continue;
            result.Add(ToInfo(id, file, meta, workspace));
            if (result.Count >= max) break;
        }
        return result;
    }

    /// <summary>The saved conversation <paramref name="id"/>, or null while agy hasn't written it.</summary>
    public static ConversationInfo? Find(string id)
    {
        if (TranscriptFile(id) is not { } file) return null;
        var meta = Read(file);
        return ToInfo(id, file, meta, History().Workspace(id, meta));
    }

    /// <summary>Whether agy has saved the conversation <paramref name="id"/>, so agy --conversation can open it.</summary>
    public static bool IsSaved(string id) => TranscriptFile(id) != null;

    /// <summary>
    /// The conversation the agy started in <paramref name="directory"/> at <paramref name="startedUtc"/> is in now, of
    /// those since then: the latest one started there (its first, or one after /new or /clear), or the latest one a prompt was
    /// sent in there (one carried on with -c or picked with /resume or /switch), whichever came last. Never one another session is in
    /// (<paramref name="taken"/>). Null when there is none.
    /// </summary>
    public static string? Live(string directory, DateTime startedUtc, IReadOnlySet<string> taken)
    {
        var dir = CodexConversations.NormalizeDir(directory);
        if (dir == null) return null;
        // A little slack: the session's clock starts just before agy's own (and the transcript's times are whole seconds).
        var since = startedUtc - TimeSpan.FromSeconds(2);
        var history = History();
        var started = Transcripts()
            .Where(t => t.File.LastWriteTimeUtc >= since)
            .Select(t => (t.Id, Meta: Read(t.File)))
            .Where(t => t.Meta.Started >= since && CodexConversations.SameDir(history.Workspace(t.Id, t.Meta), dir))
            .Select(t => (t.Id, At: t.Meta.Started!.Value));
        // agy appends prompts in order, so only the end of the history is since then.
        var prompted = history.Prompts.Reverse().TakeWhile(p => p.At >= since)
            .Where(p => p.Conversation != null && CodexConversations.SameDir(p.Workspace, dir))
            .Select(p => (Id: p.Conversation!, p.At));
        return started.Concat(prompted)
            .Where(c => !taken.Contains(c.Id))
            .OrderByDescending(c => c.At)
            .Select(c => c.Id)
            .FirstOrDefault();
    }

    /// <summary>The conversation <paramref name="id"/> as prompts and answers (for the rail and the Conversation tab), or null while agy hasn't written it.</summary>
    public static Conversation? Read(string id) =>
        TranscriptFile(id, full: true) is { } file ? ConversationReader.Read(id, file.FullName, AntigravityTranscript.Line) : null;

    private static ConversationInfo ToInfo(string id, FileInfo file, Meta meta, string? workspace)
    {
        var title = Title(id) ?? Truncate(meta.FirstPrompt, 120) ?? "(untitled)";
        return new ConversationInfo(id, workspace, null, title, Truncate(meta.FirstPrompt, 300), null, file.LastWriteTime,
            file.Length, meta.Started?.ToLocalTime(), "agy");
    }

    /// <summary>
    /// The conversation's transcript, or null when there's none. <paramref name="full"/> prefers transcript_full.jsonl,
    /// which agy writes beside it with long answers whole (transcript.jsonl cuts them at about 4 KB).
    /// </summary>
    private static FileInfo? TranscriptFile(string id, bool full = false)
    {
        if (!TerminalSession.IsConversationId(id)) return null;
        var logs = Path.Combine(BrainRoot, id, ".system_generated", "logs");
        foreach (var name in full ? new[] { "transcript_full.jsonl", "transcript.jsonl" } : ["transcript.jsonl"])
            if (new FileInfo(Path.Combine(logs, name)) is { Exists: true, Length: > 0 } file) return file;
        return null;
    }

    private static List<(string Id, FileInfo File)> Transcripts()
    {
        try
        {
            if (!Directory.Exists(BrainRoot)) return [];
            return Directory.EnumerateDirectories(BrainRoot)
                .Select(Path.GetFileName)
                .Where(TerminalSession.IsConversationId)
                .Select(id => (Id: id!, File: TranscriptFile(id!)))
                .Where(t => t.File != null)
                .Select(t => (t.Id, t.File!))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The first prompt and its time; cached until the file changes, and never again once the prompt is known.</summary>
    private static Meta Read(FileInfo file)
    {
        if (Metas.TryGetValue(file.FullName, out var cached) && (cached.Stamp == file.LastWriteTimeUtc || cached.Meta.FirstPrompt != null))
            return cached.Meta;
        var meta = new Meta(null, null);
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (stream.Position < HeadBytes && reader.ReadLine() is { } line)
                if (TryParse(line, out var e) && Str(e, "type") == AntigravityTranscript.UserInput && Str(e, "content") is { } content)
                {
                    meta = new Meta(ConversationReader.Time(e, "created_at")?.ToUniversalTime(), AntigravityTranscript.PromptText(content));
                    break;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return meta;
        }
        Metas[file.FullName] = (file.LastWriteTimeUtc, meta);
        return meta;
    }

    /// <summary>The title agy gave the conversation (annotations/&lt;id&gt;.pbtxt, a protobuf text file with a title field).</summary>
    private static string? Title(string id)
    {
        var file = new FileInfo(Path.Combine(Home, "annotations", id + ".pbtxt"));
        try
        {
            if (!file.Exists) return null;
            if (Titles.TryGetValue(id, out var cached) && cached.Stamp == file.LastWriteTimeUtc) return cached.Title;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var m = TitleField().Match(reader.ReadToEnd());
            var title = m.Success && Unescape(m.Groups[1].Value) is { Length: > 0 } t ? t : null;
            Titles[id] = (file.LastWriteTimeUtc, title);
            return title;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?m)^\s*title\s*:\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex TitleField();

    /// <summary>A protobuf text string's contents: C escapes, with bytes outside ASCII as octal (\303\251) or hex escapes of UTF-8.</summary>
    internal static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length)
            {
                var length = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(s.Substring(i, length)));
                i += length - 1;
                continue;
            }
            var c = s[++i];
            if (c is >= '0' and <= '7')
            {
                var n = 0;
                for (var k = 0; k < 3 && i < s.Length && s[i] is >= '0' and <= '7'; k++, i++) n = n * 8 + (s[i] - '0');
                i--;
                bytes.Add((byte)n);
            }
            else if (c == 'x' && i + 2 < s.Length && byte.TryParse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                bytes.Add(hex);
                i += 2;
            }
            else
                bytes.Add((byte)(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', _ => c }));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    // ---------------------------------------------------------------- history.jsonl

    /// <summary>A prompt typed into agy: when, in which folder, and the conversation it went to (null for a new conversation's first).</summary>
    private sealed record Prompt(string Text, DateTime At, string Workspace, string? Conversation);

    /// <summary>The prompts in history.jsonl, and the folder of each conversation one was sent in.</summary>
    private sealed record PromptHistory(IReadOnlyList<Prompt> Prompts, IReadOnlyDictionary<string, string> Workspaces)
    {
        public static readonly PromptHistory Empty = new([], new Dictionary<string, string>());

        /// <summary>
        /// The folder the conversation was had in: that of a prompt sent in it, or else that of the first prompt of a new
        /// conversation sent when its transcript's first prompt was, with the same text (or, when its text was shown
        /// otherwise, the only one sent then). Null for one with no prompt here (agy -p).
        /// </summary>
        public string? Workspace(string id, Meta meta)
        {
            if (Workspaces.TryGetValue(id, out var known)) return known;
            if (meta.Started is not { } started) return null;
            var near = Prompts.Where(p => p.Conversation == null && (p.At - started).Duration() <= FirstPromptSlack).ToList();
            var match = near.FirstOrDefault(p => p.Text.Trim() == meta.FirstPrompt?.Trim()) ?? (near.Count == 1 ? near[0] : null);
            return match?.Workspace;
        }
    }

    private static readonly object HistoryGate = new();
    private static (string Path, long Offset, PromptHistory Read) history = ("", 0, PromptHistory.Empty);

    /// <summary>
    /// history.jsonl, agy's prompt history: one line per prompt ({"display","timestamp" in ms,"workspace","conversationId"}),
    /// with a "type" for a slash command or a shell command, which aren't prompts. agy only appends to it, so only the
    /// lines added since the last read are read.
    /// </summary>
    private static PromptHistory History()
    {
        var path = Path.Combine(Home, "history.jsonl");
        lock (HistoryGate)
        {
            try
            {
                if (!File.Exists(path)) return (history = (path, 0, PromptHistory.Empty)).Read;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (history.Path != path || stream.Length < history.Offset) history = (path, 0, PromptHistory.Empty);   // rewritten rather than appended to
                if (stream.Length == history.Offset) return history.Read;
                var prompts = history.Read.Prompts.ToList();
                var workspaces = new Dictionary<string, string>(history.Read.Workspaces, StringComparer.OrdinalIgnoreCase);
                var offset = history.Offset;
                stream.Seek(offset, SeekOrigin.Begin);
                ConversationReader.ReadLines(stream, ref offset, line =>
                {
                    if (ParsePrompt(line) is not { } p) return;
                    prompts.Add(p);
                    if (p.Conversation != null) workspaces.TryAdd(p.Conversation, p.Workspace);
                });
                history = (path, offset, new PromptHistory(prompts, workspaces));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep what was read before.
            }
            return history.Read;
        }
    }

    /// <summary>The last millisecond a DateTimeOffset holds (9999-12-31T23:59:59.999Z): FromUnixTimeMilliseconds throws past it.</summary>
    private const long MaxUnixMilliseconds = 253_402_300_799_999;

    private static Prompt? ParsePrompt(ReadOnlySpan<byte> line)
    {
        JsonElement e;
        try
        {
            var reader = new Utf8JsonReader(line);
            e = JsonElement.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
        if (e.ValueKind != JsonValueKind.Object || Str(e, "type") != null) return null;
        if (Str(e, "workspace") is not { Length: > 0 } workspace ||
            !e.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.Number || !ts.TryGetInt64(out var ms) ||
            ms is < 0 or > MaxUnixMilliseconds) return null;
        var conversation = Str(e, "conversationId") is { } id && TerminalSession.IsConversationId(id) ? id : null;
        return new Prompt(Str(e, "display") ?? "", DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, workspace, conversation);
    }

    private static bool TryParse(string line, out JsonElement element)
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

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Truncate(string? s, int max)
    {
        if (s == null) return null;
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
