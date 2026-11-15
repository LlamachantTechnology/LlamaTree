using LlamaTree.Terminal;

namespace LlamaTree.Sessions;

/// <summary>
/// xAI's Grok CLI (grok). Like Claude, the app gives a new session's conversation its id (--session-id) and reopens it
/// with --resume; grok records which conversation each of its processes is in (see <see cref="GrokConversations"/>).
/// It only runs MCP servers registered in its own settings, so panels reach it through the panel bridge, registered
/// once with "grok mcp add", and its tools are allowed for each session with --allow.
/// </summary>
internal sealed class GrokAgent : AgentProfile
{
    public override string Id => "grok";
    public override string Name => "Grok";
    public override IReadOnlyList<string> Programs { get; } = ["grok", "grok.exe"];
    public override AgentColor Color { get; } = new("#b4b4b4", "#4d4d4d");
    public override string Follows => "through /new and /resume";

    /// <summary>
    /// Grok asks with a list of choices in place of its prompt box, under a key hint starting "1/5:select" (the choice
    /// picked, of how many); while it waits for a prompt the hint is "Shift+Tab:mode" (grok 1.0.77). Scrolling back
    /// through the conversation (Esc) leaves the question waiting, with "Tab/Space:permission" to return to it.
    /// </summary>
    public override AskingScreen Asks { get; } = new(@"^\s*(?:\d+/\d+:select|Tab/Space:permission)\b");

    public override CommandShape Command { get; } = new()
    {
        Subcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "agent", "clone", "completions", "dashboard", "doctor", "du", "disk-usage", "export", "help", "inspect", "leader", "login",
            "logout", "mcp", "memory", "models", "plugin", "sessions", "setup", "trace", "update", "usage", "version", "v", "worker",
            "worktree", "wrap",
        },
        PrintOptions = ["-p", "--single", "--prompt-file", "--prompt-json"],
        ContinueOptions = ["-c", "--continue"],
        Resume = new(["--resume", "-r"]),
        OwnConversationOptions = ["--session-id", "-s", "--fork-session"],
        NewConversationWithId = "--session-id",
        OwnConversationPattern = @"\s+(?:-r|--resume|-s|--session-id|--fork-session)(?:=\S+|\s+[^-\s]\S*)?(?=\s|$)",
    };

    /// <summary>The bridge in grok's settings; its tools allowed for this session with --allow (repeatable, so the command's own rules stay).</summary>
    public override PanelConnection Panels { get; } = new PanelConnection.Registered("grok mcp list --json",
        program => $"grok mcp add {LlamaTree.Panels.PanelMcp.ServerName} -- {CommandLineResolver.QuotePath(program)} {LlamaTree.Panels.PanelBridge.Argument}",
        _ => "--allow mcp__" + LlamaTree.Panels.PanelMcp.ServerName);

    public override IConversationStore Conversations { get; } = new GrokConversationStore();

    public override string CompactCommand => "/compact";
}
