using System.Text;
using System.Text.Json;
using LlamaTree.Panels;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>The same checks for every agent: how its command line is read, and what each start's command line is.</summary>
public class AgentCommandTests
{
    private const string Id = "01a1891c-aad9-7a73-8004-10a6cabcf58e";
    private const string Other = "0b0e8c52-5e8e-4a59-9a3c-7d1c3e0f6a11";

    /// <summary>Saved conversations as a test says.</summary>
    private sealed class Facts(Func<string, bool>? saved = null, bool folderHasOne = true) : IConversationFacts
    {
        public bool IsSaved(string id) => saved?.Invoke(id) ?? false;
        public string? ResumeTarget(string id) => id;
        public bool FolderHasConversation() => folderHasOne;
    }

    private static string Build(AgentProfile agent, string command, Launch launch, Func<string, bool>? saved = null) =>
        CommandBuilder.Build(agent, command, launch, new Facts(saved));

    private static readonly Launch Plain = new("D:\\work");

    public static TheoryData<string> AgentIds => new(Agents.All.Select(a => a.Id));

    private static AgentProfile Agent(string id) => Agents.All.Single(a => a.Id == id);

    [Theory]
    [MemberData(nameof(AgentIds))]
    public void Its_plain_command_is_tracked_and_a_print_or_subcommand_is_left_alone(string id)
    {
        var agent = Agent(id);
        var program = agent.Programs[0];
        Assert.True(agent.CanTrackConversation(program));
        Assert.True(agent.CanTrackConversation(program + " --model x"));
        var print = agent.Command.PrintOptions.Select(p => $"{program} {p} hello")
            .Concat(agent.Command.Subcommands.Take(3).Select(s => $"{program} {s}"));
        foreach (var command in print)
        {
            Assert.False(agent.CanTrackConversation(command), command);
            Assert.Equal(command, Build(agent, command, Plain with { Conversation = Id }, _ => true));
        }
    }

    [Theory]
    [MemberData(nameof(AgentIds))]
    public void A_command_that_reopens_a_conversation_hands_it_to_the_session(string id)
    {
        var agent = Agent(id);
        var program = agent.Programs[0];
        var resume = agent.Command.Resume;
        var typed = resume.IsSubcommand ? $"{program} {resume.Names[0]} {Id} --model x" : $"{program} --model x {resume.Names[0]} {Id}";
        var (rest, conversation) = agent.TakeConversation(typed);
        Assert.Equal(Id, conversation);
        Assert.Equal($"{program} --model x", rest);
        Assert.True(agent.CarriesOnConversation(typed));
        Assert.False(agent.CanTrackConversation(typed));
        Assert.Equal((program + " --model x", (string?)null), agent.TakeConversation(program + " --model x"));
    }

    [Theory]
    [MemberData(nameof(AgentIds))]
    public void Its_own_conversation_is_reopened_once_saved_without_continue(string id)
    {
        var agent = Agent(id);
        var program = agent.Programs[0];
        var command = agent.Command.ContinueOptions.Count > 0 ? $"{program} {agent.Command.ContinueOptions[^1]} --model x" : $"{program} --model x";
        var line = Build(agent, command, Plain with { Conversation = Id }, _ => true);
        var reopen = agent.Command.Resume.Names[0] + " " + Id;
        Assert.Contains(reopen, line);
        Assert.Contains("--model x", line);
        foreach (var c in agent.Command.ContinueOptions) Assert.DoesNotContain(" " + c + " ", line + " ");
        // Once it has a conversation of its own, a command that reopens another keeps its choice.
        var picks = agent.Command.Resume.IsSubcommand ? $"{program} {agent.Command.Resume.Names[0]} {Other}" : $"{program} {agent.Command.Resume.Names[0]} {Other}";
        Assert.Equal(picks, Build(agent, picks, Plain with { Conversation = Id }, _ => true));
    }

    [Theory]
    [MemberData(nameof(AgentIds))]
    public void Before_it_is_saved_the_app_picks_the_id_only_where_the_agent_lets_it(string id)
    {
        var agent = Agent(id);
        var program = agent.Programs[0];
        var line = Build(agent, program, Plain with { Conversation = Id });
        if (agent.Command.NewConversationWithId is { } option)
        {
            Assert.True(agent.ChoosesConversationId);
            Assert.Equal($"{program} {option} {Id}", line);
        }
        else
        {
            Assert.False(agent.ChoosesConversationId);
            Assert.Equal(program, line);
        }
    }

    [WindowsTheory]
    [MemberData(nameof(AgentIds))]
    public void A_session_from_a_panel_starts_a_new_conversation_on_its_prompt(string id)
    {
        var agent = Agent(id);
        var program = agent.Programs[0];
        var command = agent.Command.ContinueOptions.Count > 0 ? $"{program} {agent.Command.ContinueOptions[0]}" : program;
        var line = Build(agent, command, Plain with { NewConversation = true, FirstPrompt = "fix it" }, _ => true);
        var prompt = (agent.Command.FirstPromptOption is { } o ? o + " " : "") + "\"fix it\"";
        Assert.StartsWith($"{program} {prompt}", line);
        foreach (var c in agent.Command.ContinueOptions) Assert.DoesNotContain(" " + c, line[(program.Length + prompt.Length + 1)..] + " ");
    }

    [Theory]
    [InlineData("agy", "agy --conversation " + Id)]
    [InlineData("agy -c", "agy --conversation " + Id)]
    [InlineData("agy --conversation " + Other, "agy --conversation " + Other)]
    [InlineData("agy -p hello", "agy -p hello")]
    [InlineData("agy mcp list", "agy mcp list")]
    public void Agy_reopens_its_conversation(string command, string expected) =>
        Assert.Equal(expected, Build(Agents.Antigravity, command, Plain with { Conversation = Id }, _ => true));

    [Theory]
    [InlineData(false, "grok --session-id " + Id)]
    [InlineData(true, "grok --resume " + Id)]
    public void Grok_starts_in_the_id_the_app_picks_then_resumes_it(bool saved, string expected) =>
        Assert.Equal(expected, Build(Agents.Grok, "grok", Plain with { Conversation = Id }, _ => saved));

    [Fact]
    public void Grok_allows_the_panel_tools_and_registers_the_bridge()
    {
        var line = Build(Agents.Grok, "grok --allow Read", Plain with { Panels = new PanelLink(null, "http://127.0.0.1:5000/mcp", "t") });
        Assert.Equal("grok --allow Read --allow mcp__llamatree", line);
        var registered = Assert.IsType<PanelConnection.Registered>(Agents.Grok.Panels);
        Assert.EndsWith(" mcp-bridge", registered.AddCommand(@"C:\Apps\LlamaTree.exe"));
        Assert.StartsWith("grok mcp add llamatree -- ", registered.AddCommand(@"C:\Apps\LlamaTree.exe"));
    }

    [Fact]
    public void A_registered_bridge_finds_the_panel_server_in_the_session_environment()
    {
        var env = new Dictionary<string, string> { [PanelConnection.UrlVariable] = "inherited", [PanelConnection.TokenVariable] = "inherited" };
        Agents.Antigravity.AddEnvironment(env, Plain with { Panels = new PanelLink(null, "http://127.0.0.1:5000/mcp", "tok") });
        Assert.Equal("http://127.0.0.1:5000/mcp", env[PanelConnection.UrlVariable]);
        Assert.Equal("tok", env[PanelConnection.TokenVariable]);
        Agents.Antigravity.AddEnvironment(env, Plain);   // no panels: not another LlamaTree's either
        Assert.Empty(env);
    }

    [Fact]
    public void Agy_settings_keep_your_permissions_and_allow_the_panel_tools()
    {
        var json = AntigravityAgent.PanelSettings("""{ "permissions": { "allow": ["read"], "deny": ["rm"] }, "theme": "x", }""");
        using var doc = JsonDocument.Parse(json);
        var permissions = doc.RootElement.GetProperty("permissions");
        Assert.Equal(["read", AntigravityAgent.PanelToolsRule], permissions.GetProperty("allow").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("rm", permissions.GetProperty("deny")[0].GetString());
        Assert.False(doc.RootElement.TryGetProperty("theme", out _));
        Assert.Contains(AntigravityAgent.PanelToolsRule, AntigravityAgent.PanelSettings("not json"));
    }
}

/// <summary>Finding the conversation a running session is in.</summary>
public class LiveConversationTests
{
    private static readonly DateTime Start = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_latest_in_the_folder_since_the_start_that_no_other_session_has()
    {
        var dir = Path.GetFullPath(Path.GetTempPath());
        ConversationActivity[] seen =
        [
            new("old", dir, Start.AddMinutes(-5)),
            new("a", dir, Start.AddSeconds(10)),
            new("b", dir + Path.DirectorySeparatorChar, Start.AddSeconds(30)),
            new("elsewhere", Path.Combine(dir, "x"), Start.AddSeconds(40)),
            new("taken", dir, Start.AddSeconds(50)),
        ];
        Assert.Equal("b", LatestInFolder.Pick(_ => seen, dir, Start, new HashSet<string> { "taken" }));
        Assert.Null(LatestInFolder.Pick(_ => seen, dir, Start.AddMinutes(5), new HashSet<string>()));
    }

    [Fact]
    public void The_first_way_that_knows()
    {
        var way = new FirstOf(new NamedByProcess(_ => null), new NamedByProcess(_ => "second"), new NamedByProcess(_ => "third"));
        Assert.Equal("second", way.Find(null!, [], new HashSet<string>()));
    }
}

/// <summary>Registering the panel bridge with an agent, and the bridge itself.</summary>
public class PanelBridgeTests
{
    private const string Program = @"C:\Program Files\LlamaTree\LlamaTree.exe";

    [WindowsTheory]
    [InlineData("No MCP servers configured.", "Missing")]
    [InlineData("figma  http  enabled  https://figma.com/mcp", "Missing")]
    [InlineData("llamatree  stdio  enabled  \"C:\\Program Files\\LlamaTree\\LlamaTree.exe\" mcp-bridge", "Ours")]
    [InlineData("llamatree  stdio  disabled  \"D:\\old\\LlamaTree.exe\" mcp-bridge", "Disabled")]
    [InlineData("llamatree  stdio  enabled  D:\\Dev Builds\\LlamaTree.dev\\LlamaTree\\bin\\LlamaTree.exe mcp-bridge", "AnotherCopy")]
    [InlineData("llamatree  stdio  enabled  D:\\gone\\LlamaTree.exe mcp-bridge", "Gone")]
    [InlineData("llamatree  http  enabled  https://example.com/mcp", "Unrecognised")]
    [InlineData("llamatreex stdio enabled x", "Missing")]
    public void Only_a_missing_bridge_or_one_whose_LlamaTree_is_gone_is_added(string listed, string expected) =>
        Assert.Equal(expected, PanelRegistration.Check(listed, Program, path => !path.Contains("gone")).ToString());

    /// <summary>On a server: the Linux packages put LlamaTree in /opt/llamatree (see tools/package-linux.cs).</summary>
    [Theory]
    [InlineData("llamatree  stdio  enabled  /opt/llamatree/LlamaTree mcp-bridge", "Ours")]
    [InlineData("llamatree  stdio  enabled  /home/me/llamatree build/LlamaTree mcp-bridge", "AnotherCopy")]
    [InlineData("llamatree  stdio  enabled  /tmp/gone/LlamaTree mcp-bridge", "Gone")]
    [InlineData("""[{"name":"llamatree","command":"/tmp/gone/LlamaTree","args":["mcp-bridge"],"enabled":true}]""", "Gone")]
    [InlineData("""[{"name":"llamatree","command":"/opt/llamatree/LlamaTree","args":["mcp-bridge"],"enabled":true,"scope":"user"}]""", "Ours")]
    public void On_a_server_the_bridge_is_checked_the_same_way(string listed, string expected) =>
        Assert.Equal(expected, PanelRegistration.Check(listed, "/opt/llamatree/LlamaTree", path => path is "/home/me/llamatree build/LlamaTree").ToString());

    [UnixFact]
    public void On_a_server_the_bridge_is_registered_with_a_shell_quoted_path()
    {
        var registered = Assert.IsType<PanelConnection.Registered>(Agents.Antigravity.Panels);
        var add = registered.AddCommand("/opt/llama tree/LlamaTree");
        Assert.StartsWith("agy mcp add llamatree '/opt/llama tree/LlamaTree'", add);
        Assert.EndsWith(" mcp-bridge", add);
    }

    [WindowsFact]
    public void A_list_in_JSON_is_read_by_entry()
    {
        var listed = JsonSerializer.Serialize(new object[]
        {
            new { name = "other", command = "x", enabled = false, status = "disabled" },
            new { name = "llamatree", command = @"D:\gone\LlamaTree.exe", args = new[] { "mcp-bridge" } },
        });
        Assert.Equal(BridgeRegistration.Gone, PanelRegistration.Check(listed, Program, _ => false));
        Assert.Equal(BridgeRegistration.Missing, PanelRegistration.Check("[]", Program, _ => true));
        Assert.Equal(BridgeRegistration.Ours, PanelRegistration.Check(JsonSerializer.Serialize(new { llamatree = new { command = Program, args = new[] { "mcp-bridge" } } }), Program, _ => true));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/mcp", true)]
    [InlineData("http://localhost:5000/mcp", false)]
    [InlineData("https://127.0.0.1:5000/mcp", false)]
    [InlineData("http://127.0.0.1:5000/other", false)]
    [InlineData("http://evil.example/mcp", false)]
    [InlineData(null, false)]
    public void It_only_talks_to_this_machines_panel_server(string? url, bool expected) => Assert.Equal(expected, PanelBridge.IsPanelServer(url));

    [Fact]
    public async Task Outside_a_session_it_starts_cleanly_with_no_tools()
    {
        var output = await Run("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"show_panel"}}
            """, url: null, token: null);
        Assert.Equal(3, output.Count);
        var byId = output.ToDictionary(o => o.GetProperty("id").GetInt32());
        Assert.Equal("2025-06-18", byId[1].GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal(0, byId[2].GetProperty("result").GetProperty("tools").GetArrayLength());
        Assert.True(byId[3].TryGetProperty("error", out _));
    }

    [Fact]
    public async Task In_a_session_it_relays_each_message_with_the_token()
    {
        var handler = new Recorder();
        var output = await Run("""
            {"jsonrpc":"2.0","id":1,"method":"tools/list"}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            """, "http://127.0.0.1:5000/mcp", "tok", handler);
        Assert.Equal(2, handler.Seen.Count);
        Assert.All(handler.Seen, r => Assert.Equal("Bearer tok", r));
        var reply = Assert.Single(output);
        Assert.Equal(1, reply.GetProperty("id").GetInt32());
    }

    private static async Task<List<JsonElement>> Run(string lines, string? url, string? token, HttpMessageHandler? handler = null)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(lines.ReplaceLineEndings("\n") + "\n"));
        using var output = new MemoryStream();
        await PanelBridge.RunAsync(input, output, url, token, handler, TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    /// <summary>A panel server that answers requests with their id and notifications with 202, noting each Authorization header.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Seen) Seen.Add(request.Headers.Authorization!.ToString());
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("id", out var id)) return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":{}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
