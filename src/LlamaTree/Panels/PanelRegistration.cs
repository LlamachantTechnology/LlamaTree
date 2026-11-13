using System.Collections.Concurrent;
using LlamaTree.Sessions;

namespace LlamaTree.Panels;

/// <summary>
/// Connects panels to an agent that only takes MCP servers from its own config (agy, see <see cref="AgentProfile.McpListCommand"/>):
/// before its first session starts in a run, registers the panel bridge (<see cref="PanelBridge"/>) with the agent's own
/// "mcp add" unless it's there already. Claude and Codex get the panel server on their command line instead.
/// </summary>
internal static class PanelRegistration
{
    private static readonly ConcurrentDictionary<string, Lazy<Task>> Done = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers the bridge with <paramref name="agent"/> once a run; a failure is logged, and tried again at its next start.</summary>
    public static Task EnsureAsync(AgentProfile agent, string directory)
    {
        if (agent.McpListCommand == null || Environment.ProcessPath is not { } program) return Task.CompletedTask;
        Lazy<Task>? attempt = null;
        attempt = new Lazy<Task>(() => AttemptAsync(agent, directory, program, attempt!));
        return Done.GetOrAdd(agent.Id, attempt).Value;
    }

    /// <summary>One attempt, shared by every session that starts meanwhile: logged once if it fails, and forgotten so the next start tries again.</summary>
    private static async Task AttemptAsync(AgentProfile agent, string directory, string program, Lazy<Task> attempt)
    {
        try
        {
            await RegisterAsync(agent, directory, program);
        }
        catch (Exception ex)
        {
            Done.TryRemove(new KeyValuePair<string, Lazy<Task>>(agent.Id, attempt));
            AppLog.Warn($"panels: couldn't add LlamaTree's MCP server to {agent.Name}: {ex.Message}");
        }
    }

    private static async Task RegisterAsync(AgentProfile agent, string directory, string program)
    {
        var listed = await HeadlessAgent.RunCommandAsync(agent, agent.McpListCommand!, directory, "", "list its MCP servers", CancellationToken.None);
        if (!NeedsAdding(listed, program)) return;
        await HeadlessAgent.RunCommandAsync(agent, agent.McpAddCommand(program)!, directory, "", "add LlamaTree's MCP server", CancellationToken.None);
        AppLog.Info($"panels: added LlamaTree's MCP server to {agent.Name} ({program})");
    }

    /// <summary>
    /// From the agent's list of MCP servers ("NAME TYPE STATUS COMMAND/URL" rows): whether the bridge has to be added,
    /// or pointed at <paramref name="program"/> (LlamaTree moved, or another copy added it). A llamatree server you
    /// disabled is left alone.
    /// </summary>
    internal static bool NeedsAdding(string listed, string program)
    {
        var row = listed.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => string.Equals(Columns(l).FirstOrDefault(), PanelMcp.ServerName, StringComparison.OrdinalIgnoreCase));
        if (row == null) return true;
        if (Columns(row).Contains("disabled", StringComparer.OrdinalIgnoreCase)) return false;
        return !row.Contains(program, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>A row's columns, split on spaces or tabs.</summary>
    private static string[] Columns(string row) => row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
