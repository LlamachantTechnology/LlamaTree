using System.Globalization;
using System.Text.Json;
using LlamaTree.Platform;
using static LlamaTree.Sessions.JsonLines;

namespace LlamaTree.Sessions;

/// <summary>
/// Reads Grok's saved conversations ("sessions"), in GROK_HOME or ~/.grok:
/// - sessions/&lt;encoded folder&gt;/&lt;id&gt;/summary.json: the folder (info.cwd), title, and when it was created and updated;
/// - …/updates.jsonl: the conversation as Agent Client Protocol session updates (see <see cref="GrokTranscript"/>);
/// - …/prompt_context.json: is_non_interactive marks a grok -p run, which isn't one to resume;
/// - active_sessions.json: the conversation each running grok process is in.
/// </summary>
internal static class GrokConversations
{
    /// <summary>Grok's folder: GROK_HOME, or ~/.grok (tests point it elsewhere).</summary>
    internal static string Home
    {
        get => home ?? (Environment.GetEnvironmentVariable("GROK_HOME") is { Length: > 0 } set
            ? set : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok"));
        set => home = value;
    }

    private static string? home;

    /// <summary>What summary.json says.</summary>
    private sealed record Summary(string Id, string? Cwd, string? Title, DateTime Created, DateTime Updated, bool Interactive, string Folder);

    /// <summary>Grok's conversations with something said in them, latest first; only those had in <paramref name="directory"/> when one is given.</summary>
    public static List<ConversationInfo> List(string? directory, int max)
    {
        var folder = PathText.NormalizeFolder(directory);
        return Summaries()
            .Where(s => s.Interactive && (folder == null || PathText.SameFolder(s.Cwd, folder)) && Updates(s.Folder) is { Length: > 0 })
            .OrderByDescending(s => s.Updated)
            .Take(max)
            .Select(ToInfo)
            .ToList();
    }

    /// <summary>The conversation <paramref name="id"/>, or null while Grok hasn't saved it.</summary>
    public static ConversationInfo? Find(string id) => FindSummary(id) is { } s ? ToInfo(s) : null;

    /// <summary>Whether Grok has saved <paramref name="id"/> (it does as soon as it opens one), so grok --resume can open it.</summary>
    public static bool IsSaved(string id) => FindSummary(id) != null;

    /// <summary>The conversation the grok process among <paramref name="processIds"/> is in, from active_sessions.json; the latest opened if several.</summary>
    public static string? ForProcesses(IEnumerable<int> processIds)
    {
        var pids = processIds.ToHashSet();
        if (pids.Count == 0) return null;
        try
        {
            var file = Path.Combine(Home, "active_sessions.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            return doc.RootElement.EnumerateArray()
                .Where(e => e.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var n) && pids.Contains(n))
                .Select(e => (Id: Str(e, "session_id"), At: Str(e, "opened_at")))
                .Where(e => TerminalSession.IsConversationId(e.Id))
                .OrderByDescending(e => e.At, StringComparer.Ordinal)   // ISO 8601, UTC
                .Select(e => e.Id)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Interactive conversations updated since <paramref name="since"/>, for when grok's process isn't the session's own (see <see cref="GrokConversationStore"/>).</summary>
    internal static IEnumerable<ConversationActivity> RecentActivity(DateTime since) =>
        Summaries().Where(s => s.Interactive && s.Updated >= since).Select(s => new ConversationActivity(s.Id, s.Cwd, s.Updated));

    /// <summary>The conversation as prompts and answers, or null while there's nothing in it.</summary>
    public static Conversation? Read(string id) =>
        FindSummary(id) is { } s && Updates(s.Folder) is { Length: > 0 } file ? ConversationReader.Read(id, file.FullName, GrokTranscript.Line) : null;

    private static ConversationInfo ToInfo(Summary s) =>
        new(s.Id, s.Cwd, null, s.Title ?? "(untitled)", null, null, s.Updated.ToLocalTime(), Updates(s.Folder)?.Length ?? 0,
            s.Created.ToLocalTime(), Agents.Grok.Id);

    private static FileInfo? Updates(string folder) => new FileInfo(Path.Combine(folder, "updates.jsonl")) is { Exists: true } f ? f : null;

    private static Summary? FindSummary(string id)
    {
        if (!TerminalSession.IsConversationId(id)) return null;
        try
        {
            var root = Path.Combine(Home, "sessions");
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateDirectories(root).Select(group => Path.Combine(group, id)).Where(Directory.Exists)
                .Select(ReadSummary).FirstOrDefault(s => s != null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<Summary> Summaries()
    {
        try
        {
            var root = Path.Combine(Home, "sessions");
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateDirectories(root).SelectMany(Directory.EnumerateDirectories)
                .Where(d => TerminalSession.IsConversationId(Path.GetFileName(d)))
                .Select(ReadSummary).OfType<Summary>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static Summary? ReadSummary(string folder)
    {
        try
        {
            var file = Path.Combine(folder, "summary.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var e = doc.RootElement;
            if (!ConversationReader.Obj(e, "info", out var info) || Str(info, "id") is not { } id || !TerminalSession.IsConversationId(id)) return null;
            var title = new[] { Str(e, "generated_title"), Str(e, "session_summary") }.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            var created = Date(e, "created_at") ?? Directory.GetCreationTimeUtc(folder);
            return new Summary(id, Str(info, "cwd"), Truncate(title, 120), created, Date(e, "updated_at") ?? created, !NonInteractive(folder), folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A grok -p run (prompt_context.json's is_non_interactive), which isn't one to resume.</summary>
    private static bool NonInteractive(string folder)
    {
        try
        {
            var file = Path.Combine(folder, "prompt_context.json");
            if (!File.Exists(file)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("is_non_interactive", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}

/// <summary>
/// Grok's conversations for the app (see <see cref="GrokConversations"/>). The session's conversation is the one
/// active_sessions.json names for its grok process; if grok runs the session in a background process of its own, the
/// latest updated in the session's folder instead.
/// </summary>
internal sealed class GrokConversationStore() : ConversationStore(new FirstOf(
    new NamedByProcess(GrokConversations.ForProcesses), new LatestInFolder(GrokConversations.RecentActivity)))
{
    public override List<ConversationInfo> List(string? directory, int max) => GrokConversations.List(directory, max);
    public override ConversationInfo? Find(string id, string directory) => GrokConversations.Find(id);
    public override bool IsSaved(string id, string directory) => GrokConversations.IsSaved(id);
    protected override Conversation? Read(string id, string directory) => GrokConversations.Read(id);
}

/// <summary>
/// Reads Grok's updates.jsonl: one Agent Client Protocol session update a line (sessionUpdate, possibly inside an
/// "update" or "params" wrapper). Text arrives in chunks, which are joined: user_message_chunk makes the prompt,
/// agent_message_chunk the answer, tool_call a tool line. Thoughts and plans aren't shown.
/// </summary>
internal static class GrokTranscript
{
    public static void Line(JsonElement e, List<ConversationTurn> turns)
    {
        var update = Update(e);
        var text = ConversationReader.Obj(update, "content", out var content) ? Str(content, "text") : null;
        switch (Str(update, "sessionUpdate"))
        {
            case "user_message_chunk" when text != null:
                // A prompt's chunks follow each other; one after the answer has started is the next prompt.
                if (turns.Count > 0 && turns[^1].Items.Count == 0)
                    turns[^1] = new ConversationTurn { Index = turns[^1].Index, Prompt = ConversationReader.Clip(turns[^1].Prompt + text, ConversationReader.MaxTextChars) };
                else if (text.Trim().Length > 0)
                    turns.Add(new ConversationTurn { Index = turns.Count, Prompt = ConversationReader.Clip(text, ConversationReader.MaxTextChars) });
                break;
            case "agent_message_chunk" when text != null && turns.Count > 0:
                var items = turns[^1].Items;
                if (items.Count > 0 && items[^1].Kind == "text")
                    items[^1] = items[^1] with { Text = ConversationReader.Clip(items[^1].Text + text, ConversationReader.MaxTextChars) };
                else if (text.Trim().Length > 0)
                    items.Add(new ConversationItem("text", ConversationReader.Clip(text, ConversationReader.MaxTextChars)));
                break;
            case "tool_call" when turns.Count > 0:
                turns[^1].Items.Add(new ConversationItem("tool",
                    ConversationReader.Clip((Str(update, "title") ?? "").ReplaceLineEndings(" ").Trim(), ConversationReader.MaxToolChars), Str(update, "kind") ?? "tool"));
                break;
        }
    }

    /// <summary>The update itself: the line, or what it wraps.</summary>
    private static JsonElement Update(JsonElement e)
    {
        if (e.TryGetProperty("sessionUpdate", out _)) return e;
        if (ConversationReader.Obj(e, "update", out var u)) return u;
        if (ConversationReader.Obj(e, "params", out var p)) return ConversationReader.Obj(p, "update", out var pu) ? pu : p;
        return e;
    }
}
