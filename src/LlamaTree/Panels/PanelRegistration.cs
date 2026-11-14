using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LlamaTree.Sessions;

namespace LlamaTree.Panels;

/// <summary>What an agent's list of MCP servers says about the panel bridge.</summary>
internal enum BridgeRegistration
{
    /// <summary>No llamatree server: it's added.</summary>
    Missing,
    /// <summary>This copy of LlamaTree.</summary>
    Ours,
    /// <summary>Another copy of LlamaTree that's still there (an installed one, a build): left alone, as any copy relays alike.</summary>
    AnotherCopy,
    /// <summary>A LlamaTree that's gone (moved, or a deleted build): pointed at this one.</summary>
    Gone,
    /// <summary>Turned off by you: left alone.</summary>
    Disabled,
    /// <summary>A llamatree server that isn't LlamaTree's bridge: yours, so left alone.</summary>
    Unrecognised,
}

/// <summary>
/// Registers the panel bridge with an agent whose panels are <see cref="PanelConnection.Registered"/>, before its first
/// session starts in a run. It only ever adds a missing server or repoints one at a LlamaTree that's gone, so separate
/// copies of LlamaTree (an installed one, a build being tried) don't keep changing your settings back and forth.
/// </summary>
internal static partial class PanelRegistration
{
    private static readonly ConcurrentDictionary<string, Lazy<Task>> Done = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers the bridge once a run; a failure is logged and tried again at the agent's next start.</summary>
    public static Task EnsureAsync(AgentProfile agent, string directory)
    {
        if (agent.Panels is not PanelConnection.Registered registered || Environment.ProcessPath is not { } program) return Task.CompletedTask;
        Lazy<Task>? attempt = null;
        attempt = new Lazy<Task>(() => AttemptAsync(agent, registered, directory, program, attempt!));
        return Done.GetOrAdd(agent.Id, attempt).Value;
    }

    private static async Task AttemptAsync(AgentProfile agent, PanelConnection.Registered registered, string directory, string program, Lazy<Task> attempt)
    {
        try
        {
            var listed = await HeadlessAgent.RunCommandAsync(agent, registered.ListCommand, directory, "", "list its MCP servers", CancellationToken.None);
            var state = Check(listed, program, File.Exists);
            if (state is not (BridgeRegistration.Missing or BridgeRegistration.Gone)) return;
            await HeadlessAgent.RunCommandAsync(agent, registered.AddCommand(program), directory, "", "add LlamaTree's MCP server", CancellationToken.None);
            AppLog.Info($"panels: added LlamaTree's MCP server to {agent.Name} ({state}, {program})");
        }
        catch (Exception ex)
        {
            Done.TryRemove(new KeyValuePair<string, Lazy<Task>>(agent.Id, attempt));
            AppLog.Warn($"panels: couldn't add LlamaTree's MCP server to {agent.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// What the agent's list of MCP servers (its text or JSON output) says about the llamatree server, for this app's
    /// <paramref name="program"/>; <paramref name="exists"/> tells whether another LlamaTree it names is still there.
    /// </summary>
    internal static BridgeRegistration Check(string listed, string program, Func<string, bool> exists)
    {
        var lines = JsonEntry(listed) ?? string.Join("\n", listed.Split('\n').Select(l => l.Trim()).Where(l => ServerName().IsMatch(l)));
        if (lines.Length == 0) return BridgeRegistration.Missing;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (lines.Contains(program, comparison)) return BridgeRegistration.Ours;
        if (lines.Contains("disabled", StringComparison.OrdinalIgnoreCase)) return BridgeRegistration.Disabled;
        if (!lines.Contains(PanelBridge.Argument, StringComparison.Ordinal)) return BridgeRegistration.Unrecognised;
        var other = ProgramPath().Match(lines);
        if (!other.Success) return BridgeRegistration.Unrecognised;
        return exists(other.Value) ? BridgeRegistration.AnotherCopy : BridgeRegistration.Gone;
    }

    /// <summary>
    /// From a list given as JSON: the llamatree server's own entry (an object with that name, or the value of a
    /// "llamatree" key) as its text, with Windows paths unescaped; "" when there's none; null when the list isn't JSON.
    /// </summary>
    private static string? JsonEntry(string listed)
    {
        var text = listed.Trim();
        if (!text.StartsWith('[') && !text.StartsWith('{')) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException) { return null; }
        return Find(root) is { } entry ? entry.ToJsonString().Replace(@"\\", @"\") : "";

        static JsonNode? Find(JsonNode? node) => node switch
        {
            JsonObject o when o["name"] is JsonValue v && v.TryGetValue<string>(out var name) &&
                string.Equals(name, PanelMcp.ServerName, StringComparison.OrdinalIgnoreCase) => o,
            JsonObject o => o.FirstOrDefault(p => string.Equals(p.Key, PanelMcp.ServerName, StringComparison.OrdinalIgnoreCase)).Value
                ?? o.Select(p => Find(p.Value)).FirstOrDefault(n => n != null),
            JsonArray a => a.Select(Find).FirstOrDefault(n => n != null),
            _ => null,
        };
    }

    [GeneratedRegex(@"(?<![\w-])" + PanelMcp.ServerName + @"(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex ServerName();

    /// <summary>
    /// A LlamaTree program's full path, which may have spaces in it: up to the first "LlamaTree" or "LlamaTree.exe" that
    /// ends a word. The program is always spelled so, which a folder called "llamatree" on the way isn't.
    /// </summary>
    [GeneratedRegex(@"(?:[A-Za-z]:\\|/).*?LlamaTree(?:\.[Ee][Xx][Ee])?(?=[""'\s,]|$)")]
    private static partial Regex ProgramPath();
}
