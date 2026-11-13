using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaTree.Terminal;

namespace LlamaTree.Sessions;

/// <summary>
/// Google's Antigravity CLI (agy). It has no option that adds an MCP server for one start, so panels reach it through
/// the stdio bridge (<see cref="Panels.PanelBridge"/>), registered with agy's own "agy mcp add" before its first
/// session (<see cref="Panels.PanelRegistration"/>); each session's environment then names this run's panel server and the session's token. The app doesn't follow
/// its conversations yet (agy keeps them in databases of its own), so a session runs agy as its command says.
/// </summary>
internal sealed class AntigravityAgent : AgentProfile
{
    public override string Id => "agy";
    public override string Name => "Antigravity";
    public override IReadOnlyList<string> Programs { get; } = ["agy", "agy.exe"];
    public override AgentColor Color { get; } = new("#34a853", "#188038");
    public override bool ChoosesConversationId => false;
    public override bool TracksConversations => false;
    public override string Follows => "only while it runs";
    public override string OwnConversationPattern => @"\s+--conversation(?:=\S+|\s+[^-\s]\S*)?(?=\s|$)";
    public override string? ContinuePattern => @"\s+(?:-c|--continue)(?=\s|$)";

    /// <summary>Subcommands: anything but the interactive interface.</summary>
    private static readonly HashSet<string> Subcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "acp", "agent", "agents", "changelog", "help", "install", "mcp", "mic-serve", "models", "plugin", "plugins", "remote-control", "update",
    };

    /// <summary>Whether the command runs agy's interactive interface: no subcommand, and not one prompt printed (-p).</summary>
    internal static bool IsInteractive(string command)
    {
        var tokens = Tokens(command).Skip(1).ToList();
        if (tokens.Count > 0 && Subcommands.Contains(tokens[0])) return false;
        string[] print = ["-p", "--print", "--prompt"];
        return !tokens.Any(t => print.Contains(t.Split('=')[0], StringComparer.OrdinalIgnoreCase));
    }

    public override bool CanTrackConversation(string command) => false;

    /// <summary>--continue or --conversation.</summary>
    public override bool CarriesOnConversation(string command)
    {
        string[] carryOn = ["-c", "--continue", "--conversation"];
        return Tokens(command).Skip(1).Any(t => carryOn.Contains(t.Split('=')[0], StringComparer.OrdinalIgnoreCase));
    }

    public override (string Command, string? Conversation) TakeConversation(string command) => (command, null);

    public override string CommandLine(string command, Launch launch) =>
        PrepareCommand(command, launch.FirstPrompt, launch.Panels != null ? PanelSettingsFile() : null);

    /// <summary>
    /// The command, starting on <paramref name="firstPrompt"/> (-i, which keeps the session going) when a panel gave
    /// one, with <paramref name="settingsFile"/> (see <see cref="PanelSettingsFile"/>) as its --settings unless it
    /// passes its own. Anything but the interactive interface is left alone.
    /// </summary>
    internal static string PrepareCommand(string command, string? firstPrompt, string? settingsFile = null)
    {
        if (Agents.For(command) is not AntigravityAgent || !IsInteractive(command)) return command.Trim();
        var result = command.Trim();
        if (firstPrompt != null) result = AfterProgram(result, "-i " + CommandLineResolver.QuoteArgument(firstPrompt));
        if (settingsFile != null && !Tokens(command).Any(t => t.Split('=')[0].Equals("--settings", StringComparison.OrdinalIgnoreCase)))
            result += " --settings " + CommandLineResolver.QuotePath(settingsFile);
        return result;
    }

    /// <summary>The permission rule that allows every tool of the panel server, as Claude's --allowedTools mcp__llamatree does.</summary>
    internal const string PanelToolsRule = "mcp(" + Panels.PanelMcp.ServerName + "/*)";

    /// <summary>
    /// The settings a session starts with (--settings, which agy uses for that session only and never saves): your own
    /// permissions from ~/.gemini/antigravity-cli/settings.json, which is only read, with <see cref="PanelToolsRule"/>
    /// added to what's allowed, so the panel tools don't ask each time and your rules hold whether agy merges these
    /// or puts them in place of yours. Written into the app's data folder; null if it can't be.
    /// </summary>
    internal static string? PanelSettingsFile()
    {
        try
        {
            var own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "settings.json");
            var content = PanelSettings(File.Exists(own) ? File.ReadAllText(own) : null);
            var path = Path.Combine(SessionStore.AppDataFolder, "agy-settings.json");
            if (File.Exists(path) && File.ReadAllText(path) == content) return path;
            // Written whole and moved into place, so two sessions starting together never read half of it.
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, content);
                File.Move(temp, path, overwrite: true);
            }
            catch (IOException) when (File.ReadAllText(path) == content) { }   // the other session's move won
            finally { File.Delete(temp); }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // agy asks before each panel tool, as it would anyway
        }
    }

    /// <summary>The JSON of <see cref="PanelSettingsFile"/>, from the text of your settings.json (null when there's none).</summary>
    internal static string PanelSettings(string? ownSettings)
    {
        JsonObject? permissions = null;
        try
        {
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            permissions = ownSettings == null ? null : JsonNode.Parse(ownSettings, documentOptions: options)?["permissions"]?.DeepClone() as JsonObject;
        }
        catch (JsonException) { }   // unreadable: just the panel rule, and agy reports its own file
        permissions ??= [];
        if (permissions["allow"] is not JsonArray allow) permissions["allow"] = allow = [];
        if (!allow.Any(r => r?.GetValueKind() == JsonValueKind.String && r.GetValue<string>() == PanelToolsRule)) allow.Add(PanelToolsRule);
        return new JsonObject { ["permissions"] = permissions }.ToJsonString();
    }

    /// <summary>Where the panel bridge finds this run's panel server, and the session's token.</summary>
    public override void AddEnvironment(IDictionary<string, string> env, Launch launch)
    {
        if (launch.Panels == null)
        {
            // Not another LlamaTree's, inherited from the session this one was started in.
            env.Remove(Panels.PanelBridge.UrlVariable);
            env.Remove(Panels.PanelBridge.TokenVariable);
            return;
        }
        env[Panels.PanelBridge.UrlVariable] = launch.Panels.Url;
        env[Panels.PanelBridge.TokenVariable] = launch.Panels.Token;
    }

    public override string McpListCommand => "agy mcp list";

    public override string McpAddCommand(string program) =>
        $"agy mcp add {Panels.PanelMcp.ServerName} {CommandLineResolver.QuotePath(program)} {Panels.PanelBridge.Argument}";

    public override string? LiveConversation(TerminalSession session, int[] processIds, IReadOnlySet<string> others) => null;

    public override List<ConversationInfo> ListConversations(string? directory, int max) => [];

    public override ConversationInfo? FindConversation(string id, string directory) => null;
}
