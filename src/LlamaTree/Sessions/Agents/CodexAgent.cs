namespace LlamaTree.Sessions;

/// <summary>
/// OpenAI's Codex CLI. Codex can't be told which id a new conversation gets, so a session learns it from the rollout its
/// Codex writes (see <see cref="CodexConversations.Live"/>) and reopens it with codex resume. Panels are added as an
/// MCP server on its command line, with the token in its environment.
/// </summary>
internal sealed class CodexAgent : AgentProfile
{
    public override string Id => "codex";
    public override string Name => "Codex";
    public override IReadOnlyList<string> Programs { get; } = ["codex", "codex.exe", "codex.cmd"];
    public override AgentColor Color { get; } = new("#4a9eff", "#1a66c9");
    public override string Follows => "through /new and /resume";

    /// <summary>
    /// Codex reports when it's waiting on you (OSC 7501 state=blocked) for approvals and questions, but not for what it can
    /// ask at start, before it reports anything: the folder trust check ("› 1. Trust and continue") and an update to
    /// install ("› 1. Update now"), whose Enter runs the update. Its prompt box starts with "› " too, so only those count.
    /// </summary>
    public override AskingScreen Asks { get; } = new(@"^\s*› ?1\. (?:Trust and continue|Update now)\b");

    /// <summary>
    /// codex exec reading the prompt from standard input ("-") and printing only its answer: not saved (--ephemeral), in a
    /// read-only sandbox, with little reasoning (it's only a summary). Codex has no switch to take its tools away; the
    /// prompt carries the diff, so it has no need of them.
    /// </summary>
    public override string HeadlessCommand => "codex exec --ephemeral -s read-only --skip-git-repo-check --color never -c model_reasoning_effort=low -";

    /// <summary>The environment variable Codex reads its panel token from (so it isn't on the command line).</summary>
    internal const string PanelTokenVariable = PanelConnection.TokenVariable;

    /// <summary>Its subcommands (anything but the interactive interface); resume and fork pick their own conversation.</summary>
    public override CommandShape Command { get; } = new()
    {
        Subcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "exec", "e", "review", "login", "logout", "mcp", "mcp-server", "plugin", "app-server", "remote-control", "app", "completion",
            "update", "doctor", "sandbox", "debug", "apply", "a", "queue", "archive", "delete", "migrate-rollouts", "unarchive",
            "cloud", "exec-server", "features", "help", "agents",
        },
        SubcommandAnywhere = true,
        Resume = new(["resume"], IsSubcommand: true),
        OwnConversationSubcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "resume", "fork" },
        OwnConversationPattern = @"\s+(?:resume|fork)(?:\s+--last|\s+[^-\s]\S*)?(?=\s|$)",
    };

    /// <summary>The panel server as an MCP server on the command line. Neither value is valid TOML, so Codex takes each as the literal text.</summary>
    public override PanelConnection Panels { get; } = new PanelConnection.CommandLine(Server,
        url => $"-c {Server}.url={url} -c {Server}.bearer_token_env_var={PanelTokenVariable}");

    private const string Server = "mcp_servers." + LlamaTree.Panels.PanelMcp.ServerName;

    public override IConversationStore Conversations { get; } = new CodexConversationStore();

    /// <summary>Codex's /compact summarises the conversation as it sees fit, and /new starts another.</summary>
    public override ConversationCommands Commands { get; } = new("/compact", false, "/new");

    /// <summary>
    /// The command line for a start of a codex session (see <see cref="CommandBuilder.Build"/>), with Codex's saved
    /// conversations given as a function so it can be checked without any.
    /// </summary>
    internal static string PrepareCommand(string command, string? conversation, Func<string, bool> isSaved, string? panelUrl = null,
        bool newConversation = false, string? firstPrompt = null) =>
        CommandBuilder.Build(Agents.Codex, command,
            new Launch("", conversation, Panels: panelUrl == null ? null : new PanelLink(null, panelUrl, ""), NewConversation: newConversation, FirstPrompt: firstPrompt),
            new Facts(isSaved));

    private sealed class Facts(Func<string, bool> isSaved) : IConversationFacts
    {
        public bool IsSaved(string id) => isSaved(id);
        public string? ResumeTarget(string id) => id;
        public bool FolderHasConversation() => true;
    }
}
