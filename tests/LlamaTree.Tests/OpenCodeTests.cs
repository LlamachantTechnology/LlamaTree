using System.Text;
using System.Text.Json;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>OpenCode's parts, checked against what opencode 1.19.6 printed and served.</summary>
public class OpenCodeTests
{
    private const string Id = "ses_debbbc904fferl8Q9281hT82X3";

    private sealed class Facts(bool saved) : IConversationFacts
    {
        public bool IsSaved(string id) => saved;
        public string? ResumeTarget(string id) => id;
        public bool FolderHasConversation() => true;
    }

    private static string Build(string command, Launch launch, bool saved = false) =>
        CommandBuilder.Build(Agents.OpenCode, command, launch, new Facts(saved));

    private static readonly Launch Plain = new("D:\\work");

    [Theory]
    [InlineData(Id, true)]
    [InlineData("ses_short", false)]
    [InlineData("ses_debbbc904fferl8Q9281hT82X3/../x", false)]
    [InlineData("ses_debbbc904ffe-l8Q9281hT82X3", false)]
    [InlineData("01a1891c-aad9-7a73-8004-10a6cabcf58e", true)]
    public void Its_ids_count_as_conversation_ids(string id, bool valid) => Assert.Equal(valid, TerminalSession.IsConversationId(id));

    [Fact]
    public void It_reopens_its_conversation_with_session_on_its_own_port()
    {
        var server = new AgentServer(43117, "pw");
        Assert.Equal($"opencode --session {Id} --port 43117", Build("opencode -c", Plain with { Conversation = Id, Server = server }, saved: true));
        Assert.Equal("opencode --port 43117", Build("opencode", Plain with { Conversation = Id, Server = server }));   // not saved: a new one
        Assert.Equal("opencode --port 4096", Build("opencode --port 4096", Plain with { Server = server }));
        Assert.Equal("opencode run hello", Build("opencode run hello", Plain with { Conversation = Id, Server = server }, saved: true));
    }

    [Fact]
    public void A_command_that_opens_a_conversation_hands_it_to_the_session()
    {
        var (rest, conversation) = Agents.OpenCode.TakeConversation($"opencode -m opencode/big-pickle -s {Id}");
        Assert.Equal(Id, conversation);
        Assert.Equal("opencode -m opencode/big-pickle", rest);
    }

    [Theory]
    [InlineData("opencode", true, 0)]
    [InlineData("opencode --port 4096", true, 4096)]
    [InlineData("opencode --port=4097 -c", true, 4097)]
    [InlineData("opencode --port 0", false, 0)]
    [InlineData("opencode run hello", false, 0)]
    [InlineData("opencode serve", false, 0)]
    public void Its_interactive_interface_serves_on_a_port_the_app_knows(string command, bool serves, int port)
    {
        var server = Agents.OpenCode.NewServer(command);
        Assert.Equal(serves, server != null);
        if (server == null) return;
        Assert.InRange(server.Port, 1, 65535);
        if (port > 0) Assert.Equal(port, server.Port);
        Assert.Equal(32, server.Password.Length);
    }

    [Fact]
    public void Its_environment_has_the_password_and_the_panel_server()
    {
        var env = new Dictionary<string, string> { [OpenCodeAgent.ConfigVariable] = """{ "model": "x/y", "permission": { "bash": "ask" }, }""" };
        Agents.OpenCode.AddEnvironment(env, Plain with { Server = new AgentServer(1, "secret"), Panels = new PanelLink(null, "http://127.0.0.1:5000/mcp", "tok") });
        Assert.Equal("secret", env[OpenCodeAgent.PasswordVariable]);
        Assert.Equal("opencode", env[OpenCodeAgent.UserVariable]);
        Assert.Equal("tok", env[PanelConnection.TokenVariable]);
        using var doc = JsonDocument.Parse(env[OpenCodeAgent.ConfigVariable]);
        var config = doc.RootElement;
        Assert.Equal("x/y", config.GetProperty("model").GetString());
        var server = config.GetProperty("mcp").GetProperty("llamatree");
        Assert.Equal("remote", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:5000/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer {env:LLAMATREE_PANEL_TOKEN}", server.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("ask", config.GetProperty("permission").GetProperty("bash").GetString());
        Assert.Equal("allow", config.GetProperty("permission").GetProperty("llamatree_*").GetString());
    }

    [Fact]
    public void A_panel_server_of_your_own_is_kept()
    {
        var own = """{ "mcp": { "llamatree": { "type": "remote", "url": "http://elsewhere" } } }""";
        using var doc = JsonDocument.Parse(OpenCodeAgent.PanelConfig(own, "http://127.0.0.1:5000/mcp"));
        Assert.Equal("http://elsewhere", doc.RootElement.GetProperty("mcp").GetProperty("llamatree").GetProperty("url").GetString());
        Assert.False(doc.RootElement.TryGetProperty("permission", out _));
    }

    [Fact]
    public void Without_panels_or_a_server_nothing_is_added()
    {
        var env = new Dictionary<string, string>();
        Agents.OpenCode.AddEnvironment(env, Plain);
        Assert.Empty(env);
    }

    /// <summary>What opencode run --format json printed for "Reply with exactly the word PINEAPPLE." on its standard input.</summary>
    private const string RunOutput = """
        {"type":"step_start","timestamp":1795633043433,"sessionID":"ses_debee343cffeBUP0e53Xg6Dt6K","part":{"id":"prt_1","messageID":"msg_1","sessionID":"ses_debee343cffeBUP0e53Xg6Dt6K","type":"step-start"}}
        {"type":"text","timestamp":1795633043578,"sessionID":"ses_debee343cffeBUP0e53Xg6Dt6K","part":{"id":"prt_2","messageID":"msg_1","sessionID":"ses_debee343cffeBUP0e53Xg6Dt6K","type":"text","text":"PINEAPPLE","time":{"start":1795633043444,"end":1795633043552}}}
        {"type":"step_finish","timestamp":1795633043579,"sessionID":"ses_debee343cffeBUP0e53Xg6Dt6K","part":{"id":"prt_3","reason":"stop","messageID":"msg_1","sessionID":"ses_debee343cffeBUP0e53Xg6Dt6K","type":"step-finish","tokens":{"total":10939,"input":10922,"output":6,"reasoning":11,"cache":{"write":0,"read":0}},"cost":0}}
        """;

    [Fact]
    public void Its_one_off_answer_is_the_last_step_text()
    {
        Assert.Equal("PINEAPPLE", Agents.OpenCode.HeadlessAnswer(RunOutput));
        var twoSteps = """{"type":"step_start"}""" + "\n" + """{"type":"text","part":{"text":"Let me look."}}""" + "\n" + RunOutput;
        Assert.Equal("PINEAPPLE", Agents.OpenCode.HeadlessAnswer(twoSteps));
        var error = Assert.Throws<InvalidOperationException>(() => Agents.OpenCode.HeadlessAnswer(
            """{"type":"error","error":{"name":"ProviderAuthError","data":{"providerID":"x","message":"No API key."}}}"""));
        Assert.Equal("No API key.", error.Message);
        Assert.Throws<InvalidOperationException>(() => Agents.OpenCode.HeadlessAnswer(""));
    }

    /// <summary>GET /session/{id}/message, cut down, with a tool call and a compaction added in OpenCode's shape.</summary>
    private const string Messages = """
        [
          {"info":{"id":"msg_a","role":"user","time":{"created":1795636344905}},"parts":[
            {"type":"text","text":"Reply with exactly OK."},
            {"type":"text","text":"Called the Read tool","synthetic":true}]},
          {"info":{"id":"msg_b","role":"assistant","time":{"created":1795636344969,"completed":1795636348852},"modelID":"big-pickle","providerID":"opencode",
                   "tokens":{"total":11461,"input":11445,"output":3,"reasoning":13,"cache":{"read":100,"write":0}}},"parts":[
            {"type":"step-start"},
            {"type":"reasoning","text":"Just output OK."},
            {"type":"tool","tool":"read","callID":"c1","state":{"status":"completed","title":"notes.txt"}},
            {"type":"text","text":"OK"},
            {"type":"step-finish","reason":"stop"}]},
          {"info":{"id":"msg_c","role":"user","time":{"created":1795636349000}},"parts":[{"type":"compaction","auto":false}]},
          {"info":{"id":"msg_d","role":"assistant","time":{"created":1795636349100}},"parts":[{"type":"text","text":"Summary of the conversation."}]},
          {"info":{"id":"msg_e","role":"user","time":{"created":1795636350000}},"parts":[{"type":"text","text":"And again"}]}
        ]
        """;

    [Fact]
    public void Its_messages_read_as_prompts_and_answers()
    {
        var conversation = OpenCodeConversations.Parse(Id, Encoding.UTF8.GetBytes(Messages));
        Assert.NotNull(conversation);
        Assert.Equal(Id, conversation.SessionId);
        Assert.Equal(["Reply with exactly OK.", "And again"], conversation.Turns.Select(t => t.Prompt));
        var first = conversation.Turns[0];
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795636344905).UtcDateTime, first.Time);
        Assert.Equal([("tool", "notes.txt", "read"), ("text", "OK", null), ("text", "Summary of the conversation.", null)],
            first.Items.Select(i => (i.Kind, i.Text, i.Tool)));
        var again = OpenCodeConversations.Parse(Id, Encoding.UTF8.GetBytes(Messages));
        Assert.Equal(conversation.Version, again!.Version);
        Assert.NotEqual(conversation.Version, OpenCodeConversations.Parse(Id, Encoding.UTF8.GetBytes(Messages.Replace("\"OK\"", "\"OK!\"")))!.Version);
        Assert.Null(OpenCodeConversations.Parse(Id, Encoding.UTF8.GetBytes("[]")));
    }

    [WindowsFact]   // OpenCode writes a Windows folder with forward slashes
    public void Its_list_leaves_out_what_suggest_ran()
    {
        var scratch = HeadlessAgent.ScratchFolder().Replace("\\", "/");
        var output = $$"""
            [
              {"id":"{{Id}}","directory":"D:/work","title":"Exact OK reply request","time_created":1795636344572,"time_updated":1795636348861},
              {"id":"ses_debee343cffeBUP0e53Xg6Dt6K","directory":"{{scratch}}","title":"Commit message","time_created":1795633043000,"time_updated":1795633043579},
              {"id":"not an id","directory":"D:\\work","title":"x","time_created":1,"time_updated":2}
            ]
            """;
        var list = OpenCodeConversations.ParseList(output);
        var only = Assert.Single(list!);
        Assert.Equal(Id, only.SessionId);
        Assert.Equal("D:\\work", only.Cwd);
        Assert.Equal("Exact OK reply request", only.Title);
        Assert.Equal("opencode", only.Agent);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795636348861).LocalDateTime, only.Modified);
    }

    [Fact]
    public void Its_events_say_whether_it_works_or_waits_on_you()
    {
        var watch = new OpenCodeWatch();
        Assert.Equal("idle", watch.State);
        static string Status(string id, string type) =>
            """{"type":"session.status","properties":{"sessionID":"@id","status":{"type":"@type"}}}""".Replace("@id", id).Replace("@type", type);
        Assert.True(watch.Apply(Status(Id, "busy")));
        Assert.Equal("working", watch.State);
        Assert.Equal(Id, watch.Conversation);
        Assert.True(watch.Apply($$$"""{"type":"permission.asked","properties":{"id":"per_1","sessionID":"{{{Id}}}","permission":"bash","patterns":[]}}"""));
        Assert.Equal("blocked", watch.State);
        Assert.True(watch.Apply($$$"""{"type":"permission.replied","properties":{"sessionID":"{{{Id}}}","requestID":"per_1","reply":"once"}}"""));
        Assert.Equal("working", watch.State);
        // A sub-agent's conversation works too, but isn't the session's.
        Assert.False(watch.Apply("""{"type":"session.created","properties":{"info":{"id":"ses_child00000000000000000000","parentID":"x"}}}"""));
        Assert.True(watch.Apply("""{"type":"session.status","properties":{"sessionID":"ses_child00000000000000000000","status":{"type":"busy"}}}"""));
        Assert.Equal(Id, watch.Conversation);
        Assert.True(watch.Apply("""{"type":"session.idle","properties":{"sessionID":"ses_child00000000000000000000"}}"""));
        Assert.True(watch.Apply(Status(Id, "idle")));
        Assert.Equal("idle", watch.State);   // nothing reported yet: only once reported working does quiet mean done
        Assert.False(watch.Apply("not json"));
        Assert.False(watch.Apply("""{"type":"server.heartbeat","properties":{}}"""));
    }
}
