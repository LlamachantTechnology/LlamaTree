using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlamaTree.Sessions;

/// <summary>
/// OpenCode (opencode, checked with 1.19.6). It can't be told which id a new conversation gets, but its interactive
/// interface serves an HTTP API, so each session starts it on a port of the app's choosing (--port) with a password of
/// its own, and follows it from there: the conversation it works in, whether it's working or waiting on you, and the
/// conversation itself (see <see cref="OpenCodeWatch"/> and <see cref="OpenCodeConversations"/>). It reopens one with
/// -s. Panels are an MCP server in config passed through OPENCODE_CONFIG_CONTENT, which OpenCode merges with its own.
/// </summary>
internal sealed class OpenCodeAgent : AgentProfile
{
    public override string Id => "opencode";
    public override string Name => "OpenCode";
    public override IReadOnlyList<string> Programs { get; } = ["opencode", "opencode.exe", "opencode.cmd"];
    public override AgentColor Color { get; } = new("#f0a35e", "#a35a12");
    public override string Follows => "to another conversation once it works in it";

    public override CommandShape Command { get; } = new()
    {
        Subcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "completion", "acp", "mcp", "attach", "run", "debug", "providers", "auth", "agent", "upgrade", "uninstall", "serve", "web",
            "models", "stats", "export", "import", "github", "pr", "session", "plugin", "plug", "db",
        },
        ContinueOptions = ["-c", "--continue"],
        Resume = new(["--session", "-s"]),
        FirstPromptOption = "--prompt",
        OwnConversationPattern = @"\s+(?:--session|-s)(?:=\S+|\s+[^-\s]\S*)?(?=\s|$)",
    };

    /// <summary>The variables OpenCode reads its server's password, and extra config, from.</summary>
    internal const string PasswordVariable = "OPENCODE_SERVER_PASSWORD";
    internal const string UserVariable = "OPENCODE_SERVER_USERNAME";
    internal const string ConfigVariable = "OPENCODE_CONFIG_CONTENT";

    public override PanelConnection Panels { get; } = new PanelConnection.ConfigVariable(ConfigVariable, PanelConfig);

    public override IConversationStore Conversations { get; } = new OpenCodeConversationStore();

    public override string CompactCommand => "/compact";

    /// <summary>
    /// The interactive interface serves its API on a free port, unless the command picks its own (--port N); with
    /// --port 0 (any port) the app can't know where, so it doesn't follow it.
    /// </summary>
    public override AgentServer? NewServer(string command)
    {
        var parsed = ParsedCommand.Parse(Command, command);
        if (!parsed.IsInteractive) return null;
        if (!parsed.Has("--port")) return AgentServer.OnFreePort();
        var tokens = parsed.Tokens;
        for (var i = 1; i < tokens.Count; i++)
        {
            var value = tokens[i].StartsWith("--port=", StringComparison.OrdinalIgnoreCase) ? tokens[i][7..]
                : tokens[i].Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (value != null && int.TryParse(value, out var port) && port is > 0 and < 65536)
                return AgentServer.OnFreePort() with { Port = port };
        }
        return null;
    }

    public override IEnumerable<string> ExtraArguments(Launch launch, ParsedCommand parsed)
    {
        if (launch.Server is { } server && !parsed.Has("--port")) yield return "--port " + server.Port;
    }

    protected override void ExtraEnvironment(IDictionary<string, string> env, Launch launch)
    {
        if (launch.Server == null) return;
        env[PasswordVariable] = launch.Server.Password;
        env[UserVariable] = "opencode";
    }

    /// <summary>Follows its server, for the session's state and conversation (see <see cref="OpenCodeWatch.Follow"/>).</summary>
    public override Task Follow(AgentServer server, Func<bool> running, Action<string> report) => OpenCodeWatch.Follow(server, running, report);

    /// <summary>
    /// opencode run reads the prompt from standard input and prints its events as JSON lines (--format json). It runs
    /// with its tools: one with every tool denied, or turned off, never answers (1.19.6). OpenCode keeps each run as a
    /// conversation: there's no switch to save nothing.
    /// </summary>
    public override string HeadlessCommand => "opencode run --format json";
    public override bool HeadlessSavesConversation => true;

    /// <summary>The text of the run's last step ("text" events after the last "step_start"); an "error" event is the reason it has none.</summary>
    public override string HeadlessAnswer(string output)
    {
        var texts = new List<string>();
        string? error = null;
        foreach (var line in output.Split('\n'))
        {
            if (!JsonLines.TryParse(line, out var e)) continue;
            switch (JsonLines.Str(e, "type"))
            {
                case "step_start":
                    texts.Clear();
                    break;
                case "text" when ConversationReader.Obj(e, "part", out var part) && JsonLines.Str(part, "text") is { } text:
                    texts.Add(text);
                    break;
                case "error":
                    error = ErrorText(e) ?? "it stopped with an error.";
                    break;
            }
        }
        if (texts.Count > 0) return string.Join("\n\n", texts).Trim();
        throw new InvalidOperationException(error ?? "it gave no answer.");
    }

    /// <summary>An error event's message: error.data.message, else error.message or error.name.</summary>
    private static string? ErrorText(JsonElement e)
    {
        if (!ConversationReader.Obj(e, "error", out var error)) return JsonLines.Str(e, "message");
        return (ConversationReader.Obj(error, "data", out var data) ? JsonLines.Str(data, "message") : null)
               ?? JsonLines.Str(error, "message") ?? JsonLines.Str(error, "name");
    }

    /// <summary>
    /// The config OpenCode merges with its own for a session: what <paramref name="own"/> (the variable as the session's
    /// environment already has it) says, plus the panel server as a remote MCP server whose header takes the session's
    /// token from its environment, and its tools allowed without asking. A server of that name you set up yourself is
    /// kept, and so are your own permissions.
    /// </summary>
    internal static string PanelConfig(string? own, string url)
    {
        JsonObject config;
        try
        {
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            config = own == null ? [] : JsonNode.Parse(own, documentOptions: options) as JsonObject ?? [];
        }
        catch (JsonException) { config = []; }   // unreadable: OpenCode would have said so; the panel server still goes in
        var name = LlamaTree.Panels.PanelMcp.ServerName;
        if (config["mcp"] is not JsonObject mcp) config["mcp"] = mcp = [];
        if (mcp[name] == null)
        {
            mcp[name] = new JsonObject
            {
                ["type"] = "remote",
                ["url"] = url,
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer {env:" + PanelConnection.TokenVariable + "}" },
            };
            // OpenCode names an MCP server's tools "<server>_<tool>".
            if (config["permission"] == null) config["permission"] = new JsonObject();
            if (config["permission"] is JsonObject permission && permission[name + "_*"] == null) permission[name + "_*"] = "allow";
        }
        return config.ToJsonString();
    }
}
