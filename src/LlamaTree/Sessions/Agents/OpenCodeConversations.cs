using System.Text.Json;
using LlamaTree.Platform;
using static LlamaTree.Sessions.JsonLines;

namespace LlamaTree.Sessions;

/// <summary>
/// Reads OpenCode's conversations ("sessions"). OpenCode keeps them in a SQLite database (opencode.db in its data folder),
/// which the app doesn't open itself: a running session's are read from that session's OpenCode, over its HTTP API (see
/// <see cref="OpenCodeApi"/>); the rest from OpenCode's own commands (opencode db, opencode export), which take most of a
/// second, so what they said is kept until the database changes. Both give the same shape (checked with 1.19.6): a session
/// is { id, directory, parentID, title, time: { created, updated } } (milliseconds), and a conversation is a list of
/// { info: { role, time, tokens… }, parts: [ { type: "text" | "tool" | "reasoning" | …, … } ] }.
/// </summary>
internal static class OpenCodeConversations
{
    /// <summary>OpenCode's data folder: $XDG_DATA_HOME/opencode, or ~/.local/share/opencode (on Windows too). Tests point it elsewhere.</summary>
    internal static string Home
    {
        get => home ?? Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "opencode");
        set => home = value;
    }

    private static string? home;

    /// <summary>Its database: OPENCODE_DB (in the data folder when it's relative), or opencode.db there.</summary>
    internal static string Database =>
        Environment.GetEnvironmentVariable("OPENCODE_DB") is { Length: > 0 } set ? Path.Combine(Home, set) : Path.Combine(Home, "opencode.db");

    /// <summary>
    /// What the database looks like on disk now, to tell whether what OpenCode's commands said is still true; null when
    /// there's no database (OpenCode never ran here). A write grows its write-ahead log, and moving the log into the
    /// database changes the database's own time, so the log's size counts but not its time: every opencode command
    /// touches that, writing nothing (1.19.6).
    /// </summary>
    internal static string? Stamp()
    {
        try
        {
            var db = new FileInfo(Database);
            if (!db.Exists) return null;
            var wal = new FileInfo(db.FullName + "-wal");
            return $"{db.Length}:{db.LastWriteTimeUtc.Ticks}:{(wal.Exists ? wal.Length : -1)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- the list, from opencode db

    private static readonly object listGate = new();
    private static (string Stamp, List<ConversationInfo> All)? listed;

    /// <summary>The columns of OpenCode's session table the list needs: its conversations, not sub-agents' or archived ones.</summary>
    internal const string ListQuery =
        "select id, directory, title, time_created, time_updated from session where parent_id is null and time_archived is null order by time_updated desc limit 2000";

    /// <summary>Its conversations, latest first; only those had in <paramref name="directory"/> when one is given.</summary>
    public static List<ConversationInfo> List(string? directory, int max)
    {
        var folder = PathText.NormalizeFolder(directory);
        return All().Where(c => folder == null || PathText.SameFolder(c.Cwd, folder)).Take(max).ToList();
    }

    /// <summary>Every conversation, as opencode db last said, asked again once the database has changed.</summary>
    private static List<ConversationInfo> All()
    {
        if (Stamp() is not { } stamp) return [];
        lock (listGate)
        {
            if (listed is { } had && had.Stamp == stamp) return had.All;
            var all = Run($"db --format json \"{ListQuery}\"") is { } output ? ParseList(output) : null;
            if (all == null) return listed?.All ?? [];   // couldn't ask: what it said last
            listed = (stamp, all);
            return all;
        }
    }

    /// <summary>The rows opencode db --format json prints, as conversations; those Suggest ran (in the app's own folder) left out.</summary>
    internal static List<ConversationInfo>? ParseList(string output)
    {
        var start = output.IndexOf('[');
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var scratch = PathText.NormalizeFolder(HeadlessAgent.ScratchFolder())!;
            return doc.RootElement.EnumerateArray()
                .Select(e => Info(Str(e, "id"), Str(e, "directory"), Str(e, "title"), Time(e, "time_created"), Time(e, "time_updated")))
                .OfType<ConversationInfo>()
                .Where(c => !PathText.SameFolder(c.Cwd, scratch))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- one conversation

    /// <summary>The conversation <paramref name="id"/>, from a running OpenCode when there is one, else from the list; null if it isn't saved.</summary>
    public static ConversationInfo? Find(string id)
    {
        if (!TerminalSession.IsConversationId(id)) return null;
        foreach (var server in OpenCodeWatch.Servers)
        {
            using var doc = OpenCodeApi.GetJson(server, "/session/" + id);
            if (doc != null) return SessionInfo(doc.RootElement);
        }
        return All().FirstOrDefault(c => c.SessionId == id);
    }

    /// <summary>
    /// Whether OpenCode has saved <paramref name="id"/>, so opencode -s can open it. One the app has seen saved counts
    /// without asking again (OpenCode doesn't forget a conversation unless it's deleted), as a session starts on the
    /// window's thread.
    /// </summary>
    public static bool IsSaved(string id)
    {
        if (seen.ContainsKey(id)) return true;
        if (Find(id) == null) return false;
        seen[id] = 0;
        return true;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> seen = new(StringComparer.Ordinal);

    /// <summary>Notes that <paramref name="id"/> is saved: a running OpenCode worked in it.</summary>
    internal static void Seen(string id) => seen[id] = 0;

    private static readonly object exportGate = new();
    private static readonly Dictionary<string, (string Stamp, Conversation? Conversation)> exported = new(StringComparer.Ordinal);

    /// <summary>
    /// The conversation as prompts and answers, from <paramref name="server"/> (the session's OpenCode, running) or any
    /// running OpenCode, else from opencode export; null while there's nothing in it.
    /// </summary>
    public static Conversation? Read(string id, string directory, AgentServer? server)
    {
        if (!TerminalSession.IsConversationId(id)) return null;
        foreach (var s in server != null ? [server] : OpenCodeWatch.Servers)
            if (OpenCodeApi.Get(s, $"/session/{id}/message?directory={OpenCodeApi.Query(directory)}") is { } body)
                return Parse(id, body);
        if (server != null) return null;   // its OpenCode is starting up or busy: next time
        if (Stamp() is not { } stamp) return null;
        lock (exportGate)
        {
            if (exported.TryGetValue(id, out var had) && had.Stamp == stamp) return had.Conversation;
            if (Run("export " + id) is not { } output) return had.Conversation;
            var start = output.IndexOf('{');
            Conversation? conversation = null;
            if (start >= 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(output[start..]);
                    if (doc.RootElement.TryGetProperty("messages", out var messages))
                        conversation = Parse(id, System.Text.Encoding.UTF8.GetBytes(messages.GetRawText()));
                }
                catch (JsonException) { }
            }
            exported[id] = (stamp, conversation);
            return conversation;
        }
    }

    /// <summary>Messages (OpenCode's JSON) as a conversation; its version is a hash of what was read, so it changes with any of it.</summary>
    internal static Conversation? Parse(string id, byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var turns = new List<ConversationTurn>();
            foreach (var message in doc.RootElement.EnumerateArray()) Message(message, turns);
            return turns.Count == 0 ? null : new Conversation(id, "opencode:" + id, Hash(body), turns);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One message: a prompt you sent starts a turn (its text parts that OpenCode didn't add itself); an answer adds its
    /// text, and a line for each tool it ran (the tool's own title for the call, else its name). Reasoning isn't shown,
    /// and a compaction's request isn't a prompt.
    /// </summary>
    private static void Message(JsonElement m, List<ConversationTurn> turns)
    {
        if (!ConversationReader.Obj(m, "info", out var info) || !m.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return;
        switch (Str(info, "role"))
        {
            case "user":
            {
                if (parts.EnumerateArray().Any(p => Str(p, "type") == "compaction")) return;
                var prompt = string.Join("\n\n", parts.EnumerateArray().Where(IsOwnText).Select(p => Str(p, "text")!.Trim()).Where(t => t.Length > 0));
                if (prompt.Length == 0) return;
                turns.Add(new ConversationTurn
                {
                    Index = turns.Count, Prompt = ConversationReader.Clip(prompt, ConversationReader.MaxTextChars),
                    Time = ConversationReader.Obj(info, "time", out var time) ? Time(time, "created") : null,
                });
                break;
            }
            case "assistant" when turns.Count > 0:
                foreach (var p in parts.EnumerateArray())
                {
                    if (IsOwnText(p) && Str(p, "text")!.Trim() is { Length: > 0 } text)
                        turns[^1].Items.Add(new ConversationItem("text", ConversationReader.Clip(text, ConversationReader.MaxTextChars)));
                    else if (Str(p, "type") == "tool" && Str(p, "tool") is { } tool)
                    {
                        var title = ConversationReader.Obj(p, "state", out var state) ? Str(state, "title") : null;
                        turns[^1].Items.Add(new ConversationItem("tool",
                            ConversationReader.Clip((title ?? "").ReplaceLineEndings(" ").Trim(), ConversationReader.MaxToolChars), tool));
                    }
                }
                break;
        }
    }

    /// <summary>A text part written by you or the model, not one OpenCode added (synthetic) or left out (ignored).</summary>
    private static bool IsOwnText(JsonElement p) =>
        Str(p, "type") == "text" && Str(p, "text") != null &&
        !(p.TryGetProperty("synthetic", out var s) && s.ValueKind == JsonValueKind.True) &&
        !(p.TryGetProperty("ignored", out var i) && i.ValueKind == JsonValueKind.True);

    // ---------------------------------------------------------------- how full it is

    /// <summary>
    /// How full the conversation is, from its running OpenCode: what its latest answer was sent and wrote (OpenCode counts
    /// cached input apart), against the model's context window from the providers OpenCode has set up.
    /// </summary>
    public static ContextUsage? Context(string id, string directory, AgentServer server)
    {
        if (OpenCodeApi.Get(server, $"/session/{id}/message?directory={OpenCodeApi.Query(directory)}") is not { } body) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            int tokens = 0, compactions = 0;
            DateTime? compactedAt = null;
            string? provider = null, model = null;
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                if (!ConversationReader.Obj(m, "info", out var info)) continue;
                if (m.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.EnumerateArray().Any(p => Str(p, "type") == "compaction"))
                {
                    compactions++;
                    compactedAt = ConversationReader.Obj(info, "time", out var time) ? Time(time, "created") : null;
                }
                if (Str(info, "role") != "assistant" || !ConversationReader.Obj(info, "tokens", out var t)) continue;
                var cache = ConversationReader.Obj(t, "cache", out var c) ? ContextTally.Int(c, "read") + ContextTally.Int(c, "write") : 0;
                var held = ContextTally.Int(t, "input") + cache + ContextTally.Int(t, "output");
                if (held == 0) continue;   // an answer still being written
                tokens = held;
                (provider, model) = (Str(info, "providerID"), Str(info, "modelID"));
            }
            if (tokens == 0 || provider == null || model == null || Window(server, directory, provider, model) is not { } window) return null;
            return new ContextUsage(tokens, window, false, compactions, compactedAt, model) { LimitIs = $"the model's {window / 1000}k window" };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> windows = new(StringComparer.Ordinal);

    /// <summary>The model's context window (limit.context of the model, among /config/providers), kept once known.</summary>
    private static int? Window(AgentServer server, string directory, string provider, string model)
    {
        var key = provider + "/" + model;
        if (windows.TryGetValue(key, out var known)) return known;
        using var doc = OpenCodeApi.GetJson(server, "/config/providers?directory=" + OpenCodeApi.Query(directory));
        if (doc == null || !doc.RootElement.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array) return null;
        foreach (var p in providers.EnumerateArray())
            if (Str(p, "id") == provider && ConversationReader.Obj(p, "models", out var models) && ConversationReader.Obj(models, model, out var m) &&
                ConversationReader.Obj(m, "limit", out var limit) && ContextTally.Int(limit, "context") is > 0 and var window)
                return windows[key] = window;
        return null;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A session object of OpenCode's API as a conversation; null for a sub-agent's.</summary>
    private static ConversationInfo? SessionInfo(JsonElement e)
    {
        if (Str(e, "parentID") is { Length: > 0 } || !ConversationReader.Obj(e, "time", out var time)) return null;
        return Info(Str(e, "id"), Str(e, "directory"), Str(e, "title"), Time(time, "created"), Time(time, "updated"));
    }

    private static ConversationInfo? Info(string? id, string? directory, string? title, DateTime? created, DateTime? updated)
    {
        if (!TerminalSession.IsConversationId(id) || updated == null) return null;
        // OpenCode writes a Windows folder with forward slashes (C:/work): as the system writes it, to compare with others.
        return new ConversationInfo(id!, PathText.NormalizeFolder(directory) ?? directory, null, Truncate(string.IsNullOrWhiteSpace(title) ? null : title, 120) ?? "(untitled)", null, null,
            updated.Value.ToLocalTime(), 0, (created ?? updated).Value.ToLocalTime(), Agents.OpenCode.Id);
    }

    /// <summary>A time in milliseconds since 1970, as OpenCode writes them.</summary>
    private static DateTime? Time(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms) && ms is > 0 and < 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;

    /// <summary>FNV-1a of the bytes.</summary>
    private static long Hash(byte[] bytes)
    {
        var h = 14695981039346656037UL;
        foreach (var b in bytes) h = (h ^ b) * 1099511628211UL;
        return (long)(h & long.MaxValue);
    }

    /// <summary>What an opencode command printed, or null when it couldn't run (OpenCode isn't installed, say).</summary>
    private static string? Run(string arguments)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return HeadlessAgent.RunCommandAsync(Agents.OpenCode, "opencode " + arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "", "read its conversations", timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// OpenCode's conversations for the app (see <see cref="OpenCodeConversations"/>). The session's conversation is the one
/// its OpenCode last started working in, from its event stream (see <see cref="OpenCodeWatch"/>).
/// </summary>
internal sealed class OpenCodeConversationStore : IConversationStore
{
    public List<ConversationInfo> List(string? directory, int max) => OpenCodeConversations.List(directory, max);
    public ConversationInfo? Find(string id, string directory) => OpenCodeConversations.Find(id);
    public bool IsSaved(string id, string directory) => OpenCodeConversations.IsSaved(id);
    public bool Shows => true;
    public bool MovesWithFolder => false;

    public string? Live(TerminalSession session, int[] processIds, IReadOnlySet<string> others)
    {
        if (OpenCodeWatch.Of(session.Server)?.Conversation is not { } id || others.Contains(id)) return null;
        OpenCodeConversations.Seen(id);
        return id;
    }

    public Conversation? ReadLive(TerminalSession session, int[] processIds) =>
        session.Info.Conversation is { } id && session.IsRunning && session.Server is { } server
            ? OpenCodeConversations.Read(id, session.Info.Directory, server) : null;

    public Conversation? ReadSaved(TerminalSession session) =>
        session.Info.Conversation is { } id ? OpenCodeConversations.Read(id, session.Info.Directory, null) : null;

    public ContextUsage? Context(TerminalSession session) =>
        session.Info.Conversation is { } id && session.IsRunning && session.Server is { } server
            ? OpenCodeConversations.Context(id, session.Info.Directory, server) : null;
}
