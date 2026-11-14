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
}
