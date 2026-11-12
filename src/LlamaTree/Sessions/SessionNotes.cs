using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaTree.Sessions;

/// <summary>
/// A session's notes: a Markdown file per session in the app's data folder, which outlasts the agent's conversation.
/// A conversation that is compacted or cleared loses detail; an agent told to keep notes (see PanelMcp's save_note and
/// read_notes) writes down what it did, decided and left open as it goes, and reads them back afterwards. The person
/// turns that on for a session, and can read and edit the notes. Hand-off notes from a fresh start are kept here too.
/// </summary>
internal sealed partial class SessionNotes(string folder)
{
    /// <summary>The longest single note, and how much of the file read_notes hands back (its latest entries).</summary>
    public const int MaxNote = 6000, MaxRead = 24_000;

    private readonly object gate = new();
    private Switches? switches;

    /// <summary>The sessions that keep notes, and those of them whose agent hasn't been told in its conversation yet (see <see cref="TakeTell"/>).</summary>
    private sealed record Switches(HashSet<string> Enabled, HashSet<string> Tell);

    private string EnabledFile => Path.Combine(folder, "enabled.json");
    private string FileOf(string session) => Path.Combine(folder, Unsafe().Replace(session, "_") + ".md");

    /// <summary>Whether the session's agent is told to keep notes.</summary>
    public bool Enabled(string session)
    {
        lock (gate) return Load().Enabled.Contains(session);
    }

    public void SetEnabled(string session, bool on)
    {
        lock (gate)
        {
            var s = Load();
            if (!(on ? s.Enabled.Add(session) : s.Enabled.Remove(session))) return;
            if (on) s.Tell.Add(session); else s.Tell.Remove(session);
            Save();
        }
    }

    /// <summary>
    /// True once after notes are turned on for the session: its agent is then told, in the conversation it's in, to keep
    /// them. The server's instructions say so too, but an agent carrying on a conversation from before keeps the
    /// instructions that conversation began with.
    /// </summary>
    public bool TakeTell(string session)
    {
        lock (gate)
        {
            if (!Load().Tell.Remove(session)) return false;
            Save();
            return true;
        }
    }

    public string Read(string session)
    {
        lock (gate)
        {
            try { return File.Exists(FileOf(session)) ? File.ReadAllText(FileOf(session)) : ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
        }
    }

    /// <summary>The latest notes, whole entries, up to <see cref="MaxRead"/> characters: what an agent reads back.</summary>
    public string ReadLatest(string session)
    {
        var all = Read(session);
        if (all.Length <= MaxRead) return all;
        var cut = all.IndexOf("\n### ", all.Length - MaxRead, StringComparison.Ordinal);
        return "(Older notes are left out.)\n" + (cut < 0 ? all[^MaxRead..] : all[cut..]);
    }

    /// <summary>The notes as the person edited them.</summary>
    public void Write(string session, string text)
    {
        lock (gate)
        {
            Directory.CreateDirectory(folder);
            if (string.IsNullOrWhiteSpace(text)) File.Delete(FileOf(session));
            else File.WriteAllText(FileOf(session), text.ReplaceLineEndings("\n").TrimEnd() + "\n\n");   // a blank line, as after each entry, so the next one starts clear of it
        }
    }

    /// <summary>Adds an entry under the time it was written (and <paramref name="heading"/>, such as "Hand-off note").</summary>
    public void Append(string session, string text, string? heading = null, DateTime? now = null)
    {
        text = text.ReplaceLineEndings("\n").Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxNote) text = text[..MaxNote] + "…";
        var title = $"### {(now ?? DateTime.UtcNow):yyyy-MM-dd HH:mm} UTC{(string.IsNullOrWhiteSpace(heading) ? "" : " · " + heading.Trim())}";
        lock (gate)
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(FileOf(session), $"{title}\n\n{text}\n\n", new UTF8Encoding(false));
        }
    }

    /// <summary>A removed session's notes go with it.</summary>
    public void Forget(string session)
    {
        lock (gate)
        {
            try { File.Delete(FileOf(session)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left behind; nothing reads it */ }
            var s = Load();
            if (s.Enabled.Remove(session) | s.Tell.Remove(session)) Save();
        }
    }

    private Switches Load()
    {
        if (switches != null) return switches;
        try { switches = File.Exists(EnabledFile) ? JsonSerializer.Deserialize<Switches>(File.ReadAllText(EnabledFile)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* none, then */ }
        return switches = new Switches(switches?.Enabled ?? [], switches?.Tell ?? []);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(EnabledFile, JsonSerializer.Serialize(switches));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* kept for this run only */ }
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();
}
