using LlamaTree.Terminal;

namespace LlamaTree.Sessions;

/// <summary>
/// Claude Code. The app gives a new session's conversation its id (--session-id), reopens it with --resume, and follows
/// Claude to another one (/clear, /resume) through ~/.claude/sessions/&lt;pid&gt;.json (see <see cref="ClaudeConversationStore"/>).
/// Panels reach it through an --mcp-config file written for each session.
/// </summary>
/// <param name="themeFile">The settings file for a theme (<see cref="ThemeSettingsFile"/>; tests give their own).</param>
internal sealed class ClaudeAgent(Func<string?, string?>? themeFile = null) : AgentProfile
{
    public override string Id => "claude";
    public override string Name => "Claude";
    public override IReadOnlyList<string> Programs { get; } = ["claude", "claude.exe"];
    public override AgentColor Color { get; } = new("#d97757", "#c15f3c");   // Claude's coral
    public override string Follows => "through /clear and /resume";

    public override CommandShape Command { get; } = new()
    {
        PrintOptions = ["-p", "--print"],
        ContinueOptions = ["-c", "--continue"],
        ContinueNeedsAConversation = true,
        Resume = new(["--resume", "-r"]),
        OwnConversationOptions = ["--session-id", "--fork-session"],
        NewConversationWithId = "--session-id",
        OwnConversationPattern = @"\s+(?:-r|--resume|--session-id|--fork-session)(?:=\S+|\s+[^-\s]\S*)?(?=\s|$)",
    };

    /// <summary>The --mcp-config file, with the panel tools allowed without asking (the first prompt goes before these option lists).</summary>
    public override PanelConnection Panels { get; } = new PanelConnection.ConfigFile("--mcp-config",
        file => "--mcp-config " + CommandLineResolver.QuotePath(file) + " --allowedTools mcp__" + LlamaTree.Panels.PanelMcp.ServerName);

    public override IConversationStore Conversations { get; } = new ClaudeConversationStore();

    public override string CompactCommand => "/compact";

    /// <summary>Haiku with no tools or MCP servers, not saved (so it doesn't appear among the conversations to resume).</summary>
    public override string HeadlessCommand => "claude -p --model haiku --tools \"\" --strict-mcp-config --no-session-persistence --output-format text";
    public override bool HeadlessHasNoTools => true;

    /// <summary>
    /// Claude asks by putting a menu where its prompt box was: a permission prompt, a question (AskUserQuestion), a plan
    /// to approve, the folder trust check. Each has a picked numbered choice ("❯ 1. Yes") or an "Esc to cancel" hint.
    /// </summary>
    public override AskingScreen Asks { get; } = new(@"^\s*[>❯›] ?\d+\. |\bEsc to cancel\b", @"^\s*─{8,}", @"^[>!❯](\s|$)");

    /// <summary>
    /// A settings file naming Claude's theme to match the app's terminals (Claude can't ask the terminal itself: the
    /// console layer swallows the question), unless the command passes its own --settings.
    /// </summary>
    public override IEnumerable<string> ExtraArguments(Launch launch, ParsedCommand parsed)
    {
        if (!parsed.Has("--settings") && (themeFile ?? ThemeSettingsFile)(launch.Theme) is { } settings)
            yield return "--settings " + CommandLineResolver.QuotePath(settings);
    }

    /// <summary>
    /// The command line for a start of a claude session (see <see cref="CommandBuilder.Build"/>), with Claude's saved
    /// conversations given as functions so it can be checked without any.
    /// </summary>
    /// <param name="resumeId">After an update, the conversation the session was in.</param>
    /// <param name="hasConversation">Whether the folder has a saved conversation (--continue is dropped otherwise).</param>
    /// <param name="themeSettings">The theme's settings file.</param>
    /// <param name="mcpConfig">The file connecting Claude to the app's panel server.</param>
    internal static string PrepareCommand(string command, string? resumeId, Func<bool> hasConversation, string? themeSettings = null,
        string? mcpConfig = null, bool newConversation = false, string? firstPrompt = null,
        string? conversation = null, Func<string, bool>? isSaved = null)
    {
        var agent = new ClaudeAgent(_ => themeSettings);
        var launch = new Launch("", conversation, resumeId, null,
            mcpConfig == null ? null : new PanelLink(mcpConfig, "", ""), newConversation, firstPrompt);
        // A session resumed after an update was in that conversation, so Claude had saved it.
        return CommandBuilder.Build(agent, command, launch, new Facts(id => id == resumeId || isSaved?.Invoke(id) == true, hasConversation));
    }

    private sealed class Facts(Func<string, bool> isSaved, Func<bool> hasConversation) : IConversationFacts
    {
        public bool IsSaved(string id) => isSaved(id);
        public string? ResumeTarget(string id) => id;
        public bool FolderHasConversation() => hasConversation();
    }

    /// <summary>The settings file that sets Claude's theme to "light" or "dark", written once into the app's data folder.</summary>
    internal static string? ThemeSettingsFile(string? theme)
    {
        if (theme is not ("light" or "dark")) return null;
        try
        {
            var path = Path.Combine(SessionStore.AppDataFolder, $"claude-theme-{theme}.json");
            var content = $$"""{"theme":"{{theme}}"}""";
            if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // Claude just uses its own theme setting
        }
    }
}
