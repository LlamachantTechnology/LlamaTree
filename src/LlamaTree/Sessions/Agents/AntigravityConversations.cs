using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlamaTree.Platform;
using static LlamaTree.Sessions.JsonLines;

namespace LlamaTree.Sessions;

/// <summary>
/// Reads Google Antigravity CLI's (agy's) saved conversations, in ~/.gemini/antigravity-cli:
/// - brain/&lt;id&gt;/.system_generated/logs/transcript_full.jsonl: one line per step (see <see cref="AntigravityTranscript"/>);
/// - annotations/&lt;id&gt;.pbtxt: the title agy gave it (title:"…", protobuf text);
/// - history.jsonl: every prompt typed, with its time and folder ("workspace") but not its conversation. A conversation's
///   folder is that of the prompt in history it started with: the same text, sent within a few seconds.
/// agy also keeps conversations in databases of its own, which aren't read.
/// </summary>
internal static partial class AntigravityConversations
{
    /// <summary>agy's folder (tests point it elsewhere).</summary>
    internal static string Home { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");

    /// <summary>How far apart a prompt's time in history.jsonl and in its transcript can be (the transcript's is whole seconds).</summary>
    private static readonly TimeSpan PromptSlack = TimeSpan.FromSeconds(5);

    /// <summary>A transcript's prompts so far: the first (text and time) and the latest's time; read on from where it stopped.</summary>
    private sealed class Prompts
    {
        public readonly object Gate = new();
        public long Offset;
        public string? First;
        public DateTime? Started, Latest;
    }

    private static readonly ConcurrentDictionary<string, Prompts> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>agy's saved conversations, latest first; only those had in <paramref name="directory"/> when one is given.</summary>
    public static List<ConversationInfo> List(string? directory, int max)
    {
        var folder = PathText.NormalizeFolder(directory);
        var history = directory == null ? null : History();
        var result = new List<ConversationInfo>();
        foreach (var (id, file) in Transcripts().OrderByDescending(t => t.File.LastWriteTimeUtc))
        {
            var prompts = Read(file);
            if (prompts.First == null) continue;
            var cwd = FolderOf(prompts, history ?? History());
            if (folder != null && !PathText.SameFolder(cwd, folder)) continue;
            result.Add(ToInfo(id, file, prompts, cwd));
            if (result.Count >= max) break;
        }
        return result;
    }

    /// <summary>The saved conversation <paramref name="id"/>, or null while agy hasn't written it.</summary>
    public static ConversationInfo? Find(string id) =>
        TranscriptFile(id) is { } file && Read(file) is { First: not null } prompts ? ToInfo(id, file, prompts, FolderOf(prompts, History())) : null;

    /// <summary>Whether agy has saved <paramref name="id"/>, so agy --conversation can open it.</summary>
    public static bool IsSaved(string id) => TranscriptFile(id) is { } file && Read(file).First != null;

    /// <summary>Conversations with a prompt sent since <paramref name="since"/> (a new one's first among them), at the latest one.</summary>
    internal static IEnumerable<ConversationActivity> RecentActivity(DateTime since)
    {
        var history = History();
        foreach (var (id, file) in Transcripts())
        {
            if (file.LastWriteTimeUtc < since) continue;
            var prompts = Read(file);
            if (prompts.Latest is { } at && at >= since) yield return new ConversationActivity(id, FolderOf(prompts, history), at);
        }
    }

    /// <summary>The conversation <paramref name="id"/> as prompts and answers, or null while agy hasn't written it.</summary>
    public static Conversation? Read(string id) =>
        TranscriptFile(id) is { } file ? ConversationReader.Read(id, file.FullName, AntigravityTranscript.Line) : null;

    private static ConversationInfo ToInfo(string id, FileInfo file, Prompts prompts, string? cwd) =>
        new(id, cwd, null, Title(id) ?? Truncate(prompts.First, 120) ?? "(untitled)", Truncate(prompts.First, 300), null,
            file.LastWriteTime, file.Length, prompts.Started?.ToLocalTime(), Agents.Antigravity.Id);

    /// <summary>
    /// How full the conversation is, from its transcript: each PLANNER_RESPONSE step says what the model was sent
    /// (input_tokens, plus cache_read_tokens read from its cache) and wrote (output_tokens), and a CHECKPOINT step is
    /// a compaction. agy doesn't write the model's window, so it's the window of the model the step names.
    /// </summary>
    public static ContextUsage? Context(string id)
    {
        if (TranscriptFile(id) is not { } file) return null;
        var t = ContextTally.Read(file.FullName, ContextLine);
        if (t.Tokens == 0 && t.Compactions == 0) return null;
        return new ContextUsage(t.Tokens, WindowOf(t.Model), false, t.Compactions, t.CompactedAt, t.Model) { LimitIs = "the model's window" };
    }

    private static void ContextLine(JsonElement e, ContextTally t)
    {
        switch (Str(e, "type"))
        {
            case "CHECKPOINT":
                t.Compacted(ConversationReader.Time(e, "created_at"));
                break;
            case "PLANNER_RESPONSE" when ContextTally.Int(e, "input_tokens") + ContextTally.Int(e, "cache_read_tokens") is > 0 and var sent:
                t.Tokens = sent + ContextTally.Int(e, "output_tokens");
                if (Str(e, "model") is { Length: > 0 } model) t.Model = model;
                break;
        }
    }

    /// <summary>The context window of the models agy offers (agy models, 1.5.9): Gemini's 1M, Claude's 200k, GPT-OSS's 128k.</summary>
    internal static int WindowOf(string? model) => model?.ToLowerInvariant() switch
    {
        { } m when m.StartsWith("gemini", StringComparison.Ordinal) => 1_048_576,
        { } m when m.StartsWith("gpt-oss", StringComparison.Ordinal) => 131_072,
        _ => 200_000,
    };

    /// <summary>The full transcript, or the shorter one agy also writes.</summary>
    private static FileInfo? TranscriptFile(string id)
    {
        if (!TerminalSession.IsConversationId(id)) return null;
        var logs = Path.Combine(Home, "brain", id, ".system_generated", "logs");
        return new[] { "transcript_full.jsonl", "transcript.jsonl" }.Select(n => new FileInfo(Path.Combine(logs, n)))
            .FirstOrDefault(f => f is { Exists: true, Length: > 0 });
    }

    private static List<(string Id, FileInfo File)> Transcripts()
    {
        try
        {
            var brain = Path.Combine(Home, "brain");
            if (!Directory.Exists(brain)) return [];
            return Directory.EnumerateDirectories(brain)
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

    /// <summary>The transcript's prompts, reading only what was added since the last time.</summary>
    private static Prompts Read(FileInfo file)
    {
        var p = Cache.GetOrAdd(file.FullName, _ => new Prompts());
        lock (p.Gate)
        {
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < p.Offset) (p.Offset, p.First, p.Started, p.Latest) = (0, null, null, null);   // rewritten
                stream.Seek(p.Offset, SeekOrigin.Begin);
                ConversationReader.ReadLines(stream, ref p.Offset, line =>
                {
                    if (!TryParse(Encoding.UTF8.GetString(line), out var e) || Str(e, "type") != AntigravityTranscript.UserInput) return;
                    if (AntigravityTranscript.PromptText(Str(e, "content")) is not { } prompt || ConversationReader.Time(e, "created_at") is not { } at) return;
                    at = at.ToUniversalTime();
                    if (p.First == null) (p.First, p.Started) = (prompt, at);
                    p.Latest = at;
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return p;
        }
    }

    // ---------------------------------------------------------------- folders, from the prompt history

    /// <summary>A prompt typed into agy: its text, when, and in which folder.</summary>
    private sealed record Typed(string Text, DateTime At, string Folder);

    private static (string Path, long Length, DateTime Stamp, List<Typed> Prompts) history = ("", -1, default, []);
    private static readonly object HistoryGate = new();

    /// <summary>The folder the conversation was had in: that of its first prompt in the history, matched by text and time.</summary>
    private static string? FolderOf(Prompts prompts, List<Typed> typed)
    {
        if (prompts.First is not { } first || prompts.Started is not { } started) return null;
        var near = typed.Where(t => (t.At - started).Duration() <= PromptSlack).ToList();
        var match = near.Where(t => SameText(t.Text, first)).MinBy(t => (t.At - started).Duration())
            ?? (near.Count == 1 ? near[0] : null);   // a long prompt may be shown shortened in the history
        return match?.Folder;
    }

    private static bool SameText(string a, string b) => string.Equals(a.Trim().ReplaceLineEndings("\n"), b.Trim().ReplaceLineEndings("\n"), StringComparison.Ordinal);

    /// <summary>history.jsonl ({"display", "timestamp" in milliseconds, "workspace"} a line), read again once it changes.</summary>
    private static List<Typed> History()
    {
        var file = new FileInfo(Path.Combine(Home, "history.jsonl"));
        lock (HistoryGate)
        {
            try
            {
                if (!file.Exists) return [];
                if (history.Path == file.FullName && history.Length == file.Length && history.Stamp == file.LastWriteTimeUtc) return history.Prompts;
                var prompts = new List<Typed>();
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                    if (TryParse(line, out var e) && Str(e, "display") is { } text && Str(e, "workspace") is { Length: > 0 } folder &&
                        e.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var ms) && ms is > 0 and < 253402300799999)
                        prompts.Add(new Typed(text, DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, folder));
                history = (file.FullName, file.Length, file.LastWriteTimeUtc, prompts);
                return prompts;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    // ---------------------------------------------------------------- titles

    /// <summary>The title agy gave the conversation, from annotations/&lt;id&gt;.pbtxt.</summary>
    private static string? Title(string id)
    {
        try
        {
            var file = Path.Combine(Home, "annotations", id + ".pbtxt");
            if (!File.Exists(file)) return null;
            return TitleField().Match(File.ReadAllText(file)) is { Success: true } m && Unescape(m.Groups[1].Value) is { Length: > 0 } title
                ? Truncate(title, 120) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s*title\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline)]
    private static partial Regex TitleField();

    /// <summary>A protobuf text string's contents: C escapes, with bytes outside ASCII as octal (\303\251) or hex (\xc3) escapes of UTF-8.</summary>
    internal static string Unescape(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
                continue;
            }
            var c = s[++i];
            switch (c)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case >= '0' and <= '7':
                    var octal = 0;
                    var n = 0;
                    for (; n < 3 && i < s.Length && s[i] is >= '0' and <= '7'; n++, i++) octal = octal * 8 + (s[i] - '0');
                    i--;
                    bytes.Add((byte)octal);
                    break;
                case 'x' when i + 2 < s.Length && byte.TryParse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex):
                    bytes.Add(hex);
                    i += 2;
                    break;
                default: bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString())); break;   // \" \\ \'
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// <summary>agy's conversations for the app (see <see cref="AntigravityConversations"/>). agy keeps a conversation's folder in its own databases too, so they don't move with a folder.</summary>
internal sealed class AntigravityConversationStore() : ConversationStore(new LatestInFolder(AntigravityConversations.RecentActivity))
{
    public override List<ConversationInfo> List(string? directory, int max) => AntigravityConversations.List(directory, max);
    public override ConversationInfo? Find(string id, string directory) => AntigravityConversations.Find(id);
    public override bool IsSaved(string id, string directory) => AntigravityConversations.IsSaved(id);
    protected override Conversation? Read(string id, string directory) => AntigravityConversations.Read(id);

    public override ContextUsage? Context(TerminalSession session) =>
        session.Info.Conversation is { } id ? AntigravityConversations.Context(id) : null;
}

/// <summary>
/// Reads an agy transcript into prompts and answers for the rail and the Conversation tab. Each line is a step:
/// USER_INPUT a prompt (the person's text is in &lt;USER_REQUEST&gt;), PLANNER_RESPONSE the model's turn (its text in
/// content, the tools it called in tool_calls). Tool output (GENERIC) and system messages aren't shown.
/// </summary>
internal static partial class AntigravityTranscript
{
    internal const string UserInput = "USER_INPUT";

    public static void Line(JsonElement e, List<ConversationTurn> turns)
    {
        switch (Str(e, "type"))
        {
            case UserInput when PromptText(Str(e, "content")) is { } prompt:
                turns.Add(new ConversationTurn { Index = turns.Count, Prompt = ConversationReader.Clip(prompt, ConversationReader.MaxTextChars), Time = ConversationReader.Time(e, "created_at") });
                break;
            case "PLANNER_RESPONSE" when turns.Count > 0:
                var items = turns[^1].Items;
                if (Str(e, "content") is { } text && !string.IsNullOrWhiteSpace(text))
                    items.Add(new ConversationItem("text", ConversationReader.Clip(text.Trim(), ConversationReader.MaxTextChars)));
                if (e.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    foreach (var call in calls.EnumerateArray())
                        items.Add(new ConversationItem("tool", ToolDetail(call), Str(call, "name") ?? "tool"));
                break;
        }
    }

    /// <summary>The person's text in a USER_INPUT step, or null for one without any (agy's own).</summary>
    internal static string? PromptText(string? content) =>
        content != null && UserRequest().Match(content) is { Success: true } m && m.Groups[1].Value.Trim() is { Length: > 0 } text ? text : null;

    [GeneratedRegex(@"<USER_REQUEST>\s*(.*?)\s*</USER_REQUEST>", RegexOptions.Singleline)]
    private static partial Regex UserRequest();

    private static readonly string[] ToolKeys = ["toolSummary", "CommandLine", "AbsolutePath", "TargetFile", "Query", "query", "Url", "url", "SearchPath", "path"];

    private static string ToolDetail(JsonElement call)
    {
        if (!ConversationReader.Obj(call, "args", out var args)) return "";
        foreach (var key in ToolKeys)
            if (Str(args, key) is { } v && !string.IsNullOrWhiteSpace(v))
                return ConversationReader.Clip(v.ReplaceLineEndings(" ").Trim(), ConversationReader.MaxToolChars);
        return "";
    }
}
