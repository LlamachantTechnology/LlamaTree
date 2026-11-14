using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaTree.Terminal;

namespace LlamaTree.Sessions;

/// <summary>
/// Google's Antigravity CLI (agy). It can't be told which id a new conversation gets, so a session learns it from agy's
/// transcripts (see <see cref="AntigravityConversations"/>) and reopens it with --conversation. agy only runs MCP servers
/// registered in its own settings, and doesn't fill in environment variables there, so panels reach it through the
/// panel bridge, registered once with "agy mcp add".
/// </summary>
internal sealed class AntigravityAgent : AgentProfile
{
    public override string Id => "agy";
    public override string Name => "Antigravity";
    public override IReadOnlyList<string> Programs { get; } = ["agy", "agy.exe", "agy.cmd"];
    public override AgentColor Color { get; } = new("#34a853", "#188038");
    public override string Follows => "to another conversation once you send a prompt in it";

    /// <summary>
    /// agy asks with a menu in place of its prompt box, whose key hint starts "↑/↓ Navigate · " (then "enter Confirm" for
    /// the folder trust check, "tab Amend" for a command to approve); while it waits for a prompt the hint is "? for shortcuts" (agy 1.5.9).
    /// An answered menu's lines stay in the conversation, above its prompt box: a rule with "&gt;" under it.
    /// </summary>
    public override AskingScreen Asks { get; } = new(@"^\s*↑/↓ Navigate · ", @"^\s*─{8,}", @"^>(\s|$)");

    public override CommandShape Command { get; } = new()
    {
        Subcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "acp", "agent", "agents", "changelog", "help", "install", "mcp", "mic-serve", "models", "plugin", "plugins", "remote-control", "update",
        },
        PrintOptions = ["-p", "--print", "--prompt"],
        ContinueOptions = ["-c", "--continue"],
        Resume = new(["--conversation"]),
        FirstPromptOption = "-i",
        OwnConversationPattern = @"\s+--conversation(?:=\S+|\s+[^-\s]\S*)?(?=\s|$)",
    };

    /// <summary>The bridge in agy's settings, and --settings allowing its tools for this session only (unless the command passes its own).</summary>
    public override PanelConnection Panels { get; } = new PanelConnection.Registered("agy mcp list",
        program => $"agy mcp add {LlamaTree.Panels.PanelMcp.ServerName} {CommandLineResolver.QuotePath(program)} {LlamaTree.Panels.PanelBridge.Argument}",
        parsed => parsed.Has("--settings") || PanelSettingsFile() is not { } file ? null : "--settings " + CommandLineResolver.QuotePath(file));

    public override IConversationStore Conversations { get; } = new AntigravityConversationStore();

    /// <summary>The permission rule that allows every tool of the panel server.</summary>
    internal const string PanelToolsRule = "mcp(" + LlamaTree.Panels.PanelMcp.ServerName + "/*)";

    /// <summary>
    /// The settings a session starts with: your own permissions from agy's settings.json (only read) with
    /// <see cref="PanelToolsRule"/> allowed too, so your rules hold whether agy merges these with yours or puts them in
    /// their place. Written into the app's data folder; null if it can't be.
    /// </summary>
    internal static string? PanelSettingsFile()
    {
        try
        {
            var own = Path.Combine(AntigravityConversations.Home, "settings.json");
            var content = PanelSettings(File.Exists(own) ? File.ReadAllText(own) : null);
            var path = Path.Combine(SessionStore.AppDataFolder, "agy-settings.json");
            if (File.Exists(path) && File.ReadAllText(path) == content) return path;
            // Written whole and moved into place, so a session starting at the same time never reads half of it.
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, content);
                File.Move(temp, path, overwrite: true);
            }
            finally { File.Delete(temp); }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // agy asks before each panel tool, as it would anyway
        }
    }

    /// <summary>The settings' JSON, from the text of your own settings.json (null when there's none).</summary>
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
        if (!allow.Any(r => r is JsonValue v && v.TryGetValue<string>(out var s) && s == PanelToolsRule)) allow.Add(PanelToolsRule);
        return new JsonObject { ["permissions"] = permissions }.ToJsonString();
    }
}
