namespace LlamaTree.Sessions;

/// <summary>
/// How an agent reaches the app's panel server (and the sessions' messages through it). Each kind is handled here once:
/// what goes on the command line, and what goes in the environment.
/// </summary>
internal abstract class PanelConnection
{
    /// <summary>The variables a session's environment names this run's panel server and the session's token in.</summary>
    public const string UrlVariable = "LLAMATREE_PANEL_URL";
    public const string TokenVariable = "LLAMATREE_PANEL_TOKEN";

    /// <summary>No panels.</summary>
    public static readonly PanelConnection None = new NoPanels();

    /// <summary>Whether the app writes a config file for each session (<see cref="PanelLink.ConfigFile"/>) for this one.</summary>
    public virtual bool NeedsConfigFile => false;

    /// <summary>The command line with the panel server added, unless the command configures it itself.</summary>
    public virtual string AddTo(string commandLine, ParsedCommand parsed, PanelLink link) => commandLine;

    /// <summary>What the agent needs in its environment for this start; <paramref name="link"/> is null without panels.</summary>
    public virtual void AddEnvironment(IDictionary<string, string> env, PanelLink? link) { }

    private sealed class NoPanels : PanelConnection;

    /// <summary>
    /// A config file written for the session, named by an option (Claude's --mcp-config), with the server's tools allowed
    /// without asking. A command that passes its own <paramref name="option"/> keeps it.
    /// </summary>
    public sealed class ConfigFile(string option, Func<string, string> arguments) : PanelConnection
    {
        public override bool NeedsConfigFile => true;

        public override string AddTo(string commandLine, ParsedCommand parsed, PanelLink link) =>
            link.ConfigFile == null || parsed.Has(option) ? commandLine : commandLine.TrimEnd() + " " + arguments(link.ConfigFile);
    }

    /// <summary>
    /// The server's address on the command line (Codex's -c mcp_servers…), with the token in <see cref="TokenVariable"/>
    /// so it isn't on the command line. A command that already mentions <paramref name="marker"/> keeps its own settings.
    /// </summary>
    public sealed class CommandLine(string marker, Func<string, string> arguments) : PanelConnection
    {
        public override string AddTo(string commandLine, ParsedCommand parsed, PanelLink link) =>
            commandLine.Contains(marker, StringComparison.OrdinalIgnoreCase) ? commandLine : commandLine.TrimEnd() + " " + arguments(link.Url);

        public override void AddEnvironment(IDictionary<string, string> env, PanelLink? link)
        {
            if (link != null) env[TokenVariable] = link.Token;
        }
    }

    /// <summary>
    /// Config for the agent written into an environment variable it merges with its own config files (OpenCode's
    /// OPENCODE_CONFIG_CONTENT), naming the server's address, with the token in <see cref="TokenVariable"/>.
    /// </summary>
    /// <param name="variable">The variable the agent reads.</param>
    /// <param name="content">The variable's value from what it already holds (null when unset) and the server's address.</param>
    public sealed class ConfigVariable(string variable, Func<string?, string, string> content) : PanelConnection
    {
        public override void AddEnvironment(IDictionary<string, string> env, PanelLink? link)
        {
            if (link == null) return;
            env[variable] = content(env.TryGetValue(variable, out var own) ? own : null, link.Url);
            env[TokenVariable] = link.Token;
        }
    }

    /// <summary>
    /// For an agent that can't be given an MCP server for one start (agy, Grok): the panel bridge
    /// (<see cref="LlamaTree.Panels.PanelBridge"/>, this app run as "mcp-bridge") registered once in the agent's own
    /// settings, with the session's environment naming this run's panel server and the session's token.
    /// </summary>
    /// <param name="listCommand">Lists the MCP servers registered with the agent ("agy mcp list").</param>
    /// <param name="addCommand">Registers the bridge, given this app's program: "agy mcp add llamatree &lt;program&gt; mcp-bridge".</param>
    /// <param name="allowTools">The arguments that let the panel tools run without asking, for this command; null for none.</param>
    public sealed class Registered(string listCommand, Func<string, string> addCommand, Func<ParsedCommand, string?> allowTools) : PanelConnection
    {
        public string ListCommand => listCommand;
        public string AddCommand(string program) => addCommand(program);

        public override string AddTo(string commandLine, ParsedCommand parsed, PanelLink link) =>
            allowTools(parsed) is { } arguments ? commandLine.TrimEnd() + " " + arguments : commandLine;

        public override void AddEnvironment(IDictionary<string, string> env, PanelLink? link)
        {
            if (link == null)
            {
                // Not another LlamaTree's, inherited from the session this one was started in.
                env.Remove(UrlVariable);
                env.Remove(TokenVariable);
                return;
            }
            env[UrlVariable] = link.Url;
            env[TokenVariable] = link.Token;
        }
    }
}
