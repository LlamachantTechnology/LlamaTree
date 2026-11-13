using System.Text.Json.Nodes;
using LlamaTree.Panels;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>Antigravity (agy) sessions: their command lines, and connecting them to panels through the bridge.</summary>
public class AntigravityTests
{
    [Theory]
    [InlineData("agy", "agy")]
    [InlineData("agy.exe --model gemini-3-pro", "agy")]
    [InlineData("agy.cmd", "agy")]
    [InlineData("agyx", "none")]
    public void The_agent_is_the_program(string command, string expected) => Assert.Equal(expected, Agents.For(command)?.Id ?? "none");

    private const string Id = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData("agy", true)]
    [InlineData("agy -c --model x", true)]
    [InlineData("agy --conversation " + Id, false)]
    [InlineData("agy --conversation=" + Id, false)]
    [InlineData("agy -p hello", false)]
    [InlineData("agy mcp list", false)]
    public void The_app_tracks_an_interactive_agy(string command, bool expected)
    {
        Assert.True(Agents.Antigravity.TracksConversations);
        Assert.Equal(expected, Agents.Antigravity.CanTrackConversation(command));
    }

    [Theory]
    [InlineData("agy --conversation " + Id + " --model x")]
    [InlineData("agy --model x --conversation=" + Id)]
    public void Conversation_typed_as_the_command_becomes_the_sessions_conversation(string command) =>
        Assert.Equal(("agy --model x", (string?)Id), Agents.Antigravity.TakeConversation(command));

    [Theory]
    [InlineData("agy --conversation not-an-id")]
    [InlineData("agy -p hi --conversation " + Id)]
    public void A_conversation_the_app_cant_keep_is_left_in_the_command(string command) =>
        Assert.Equal((command, (string?)null), Agents.Antigravity.TakeConversation(command));

    [Theory]
    [InlineData("agy -c --model x")]
    [InlineData("agy --continue --model x")]
    public void Continue_is_dropped_once_the_conversation_is_known(string command) =>
        Assert.Equal("agy --model x", Agents.Antigravity.StripContinue(command));

    [Theory]
    [InlineData("agy --model x", true, "agy --model x --conversation " + Id)]
    [InlineData("agy -c --model x", true, "agy --model x --conversation " + Id)]
    [InlineData("agy -c --model x", false, "agy -c --model x")]   // not saved yet: agy starts as its command says
    [InlineData("agy --conversation 99999999-2222-3333-4444-555555555555", true, "agy --conversation 99999999-2222-3333-4444-555555555555")]
    [InlineData("agy -p hello", true, "agy -p hello")]
    public void A_session_reopens_its_conversation_once_agy_has_saved_it(string command, bool saved, string expected) =>
        Assert.Equal(expected, AntigravityAgent.PrepareCommand(command, null, conversation: Id, isSaved: _ => saved));

    [WindowsFact]
    public void A_panels_new_conversation_isnt_the_sessions_old_one() =>
        Assert.Equal("agy -i \"fix it\" --model x",
            AntigravityAgent.PrepareCommand("agy --model x", "fix it", newConversation: true, conversation: Id, isSaved: _ => true));

    [Theory]
    [InlineData("agy -c", true)]
    [InlineData("agy --continue", true)]
    [InlineData("agy --conversation 11111111-2222-3333-4444-555555555555", true)]
    [InlineData("agy --model x", false)]
    public void Continue_and_conversation_carry_on(string command, bool expected) =>
        Assert.Equal(expected, Agents.Antigravity.CarriesOnConversation(command));

    [WindowsFact]
    public void A_panels_prompt_starts_an_interactive_session() =>
        Assert.Equal("agy -i \"fix it\" --model x", AntigravityAgent.PrepareCommand("agy --model x", "fix it"));

    [WindowsTheory]
    [InlineData("agy -c --model x")]
    [InlineData("agy --continue --model x")]
    [InlineData("agy --conversation 11111111-2222-3333-4444-555555555555 --model x")]
    [InlineData("agy --conversation=11111111-2222-3333-4444-555555555555 --model x")]
    public void A_panels_prompt_starts_a_new_conversation_not_an_old_one(string command) =>
        Assert.Equal("agy -i \"fix it\" --model x", AntigravityAgent.PrepareCommand(command, "fix it", newConversation: true));

    [Fact]
    public void Otherwise_continue_is_kept() =>
        Assert.Equal("agy -c", AntigravityAgent.PrepareCommand("agy -c", null));

    [Theory]
    [InlineData("agy -p hello")]
    [InlineData("agy --print hello")]
    [InlineData("agy mcp list")]
    public void A_command_that_isnt_interactive_is_left_alone(string command) =>
        Assert.Equal(command, AntigravityAgent.PrepareCommand(command, "fix it", "s.json"));

    [WindowsFact]
    public void The_panel_settings_are_added_unless_the_command_has_its_own()
    {
        Assert.Equal("agy --model x --settings \"C:\\d\\agy-settings.json\"", AntigravityAgent.PrepareCommand("agy --model x", null, @"C:\d\agy-settings.json"));
        Assert.Equal("agy --settings mine.json", AntigravityAgent.PrepareCommand("agy --settings mine.json", null, @"C:\d\agy-settings.json"));
    }

    [Fact]
    public void Every_panel_tool_is_allowed_with_no_settings_of_your_own()
    {
        var p = JsonNode.Parse(AntigravityAgent.PanelSettings(null))!["permissions"]!;
        Assert.Equal(["mcp(llamatree/*)"], p["allow"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public void Your_own_permissions_are_kept()
    {
        const string own = """
            {
              // comments and trailing commas are allowed in agy's file
              "model": "x",
              "permissions": { "allow": ["mcp(llamatree/show_panel)", "command(git status)",], "deny": ["command(rm)"] },
            }
            """;
        var settings = JsonNode.Parse(AntigravityAgent.PanelSettings(own))!.AsObject();
        Assert.Equal(["permissions"], settings.Select(p => p.Key));   // only permissions: nothing else of yours is repeated
        var p = settings["permissions"]!;
        Assert.Equal(["mcp(llamatree/show_panel)", "command(git status)", "mcp(llamatree/*)"], p["allow"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(["command(rm)"], p["deny"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Theory]
    [InlineData("""{"permissions":{"allow":["mcp(llamatree/*)"]}}""")]
    [InlineData("not json")]
    [InlineData("""{"permissions":{"allow":"oops"}}""")]
    public void The_panel_rule_is_there_once(string own) =>
        Assert.Single(JsonNode.Parse(AntigravityAgent.PanelSettings(own))!["permissions"]!["allow"]!.AsArray(),
            r => r!.GetValue<string>() == AntigravityAgent.PanelToolsRule);

    [Fact]
    public void Without_a_prompt_the_command_is_as_it_was() =>
        Assert.Equal("agy --model x", AntigravityAgent.PrepareCommand("  agy --model x ", null));

    [Fact]
    public void The_session_environment_names_the_panel_server_and_token()
    {
        var env = new Dictionary<string, string>();
        Agents.Antigravity.AddEnvironment(env, new Launch("C:\\work", Panels: new PanelLink(null, "http://127.0.0.1:5000/mcp", "tok")));
        Assert.Equal("http://127.0.0.1:5000/mcp", env[PanelBridge.UrlVariable]);
        Assert.Equal("tok", env[PanelBridge.TokenVariable]);
    }

    [Fact]
    public void Without_panels_another_LlamaTrees_server_isnt_passed_on()
    {
        var env = new Dictionary<string, string> { [PanelBridge.UrlVariable] = "http://127.0.0.1:6000/mcp", [PanelBridge.TokenVariable] = "outer" };
        Agents.Antigravity.AddEnvironment(env, new Launch("C:\\work"));
        Assert.Empty(env);
    }

    [WindowsFact]
    public void The_bridge_is_added_with_no_port_or_token() =>
        Assert.Equal("agy mcp add llamatree \"C:\\Program Files\\LlamaTree\\LlamaTree.exe\" mcp-bridge",
            Agents.Antigravity.McpAddCommand(@"C:\Program Files\LlamaTree\LlamaTree.exe"));

    [Fact]
    public void Only_agents_that_need_it_get_the_bridge() =>
        Assert.Equal(["agy"], Agents.All.Where(a => a.McpListCommand != null).Select(a => a.Id));
}

/// <summary>Whether agy's list of MCP servers already has the panel bridge, pointing at this copy of LlamaTree.</summary>
public class PanelRegistrationTests
{
    private const string Program = @"C:\Program Files\LlamaTree\LlamaTree.exe";

    private static string Listed(string row) => "NAME       TYPE   STATUS   COMMAND/URL\n" + row + "\n";

    [Fact]
    public void Missing_is_added() => Assert.True(PanelRegistration.NeedsAdding("No MCP servers configured.\n", Program));

    [Fact]
    public void Another_server_doesnt_count() =>
        Assert.True(PanelRegistration.NeedsAdding(Listed("llamatree-old  stdio  enabled  x.exe mcp-bridge"), Program));

    [Fact]
    public void Registered_for_this_copy_is_left_alone() =>
        Assert.False(PanelRegistration.NeedsAdding(Listed($"llamatree  stdio  enabled  {Program} mcp-bridge"), Program));

    [Fact]
    public void Registered_for_another_copy_is_pointed_here() =>
        Assert.True(PanelRegistration.NeedsAdding(Listed(@"llamatree  stdio  enabled  D:\old\LlamaTree.exe mcp-bridge"), Program));

    [Fact]
    public void Columns_split_by_tabs_count_too()
    {
        Assert.False(PanelRegistration.NeedsAdding(Listed($"llamatree\tstdio\tenabled\t{Program} mcp-bridge"), Program));
        Assert.False(PanelRegistration.NeedsAdding(Listed("llamatree\tstdio\tdisabled\tD:\\old\\LlamaTree.exe mcp-bridge"), Program));
    }

    [Fact]
    public void Disabled_by_you_stays_disabled() =>
        Assert.False(PanelRegistration.NeedsAdding(Listed(@"llamatree  stdio  disabled  D:\old\LlamaTree.exe mcp-bridge"), Program));
}
