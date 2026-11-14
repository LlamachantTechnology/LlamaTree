using System.Text.Json;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>Tests that point an agent's home folder elsewhere run one at a time.</summary>
[CollectionDefinition(nameof(AgentHomes), DisableParallelization = true)]
public sealed class AgentHomes;

/// <summary>agy's conversations, read from a folder laid out as agy writes it (from a real agy 1.5.9 conversation).</summary>
[Collection(nameof(AgentHomes))]
public sealed class AntigravityConversationTests : IDisposable
{
    private const string A = "170f5677-72da-4a87-a157-1d4c12aaef46";
    private const string B = "2a6b0f3e-0d4c-4f7e-9a31-5b8c6d7e8f90";
    private readonly TempFolder home = new();
    private readonly string before = AntigravityConversations.Home;
    private readonly string work = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agy-work"));

    public AntigravityConversationTests() => AntigravityConversations.Home = home.Path;

    public void Dispose()
    {
        AntigravityConversations.Home = before;
        home.Dispose();
    }

    private void Transcript(string id, params string[] lines)
    {
        var logs = Path.Combine(home.Path, "brain", id, ".system_generated", "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "transcript_full.jsonl"), lines);
    }

    private static string Prompt(string text, string at) =>
        JsonSerializer.Serialize(new { step_index = 0, source = "USER_EXPLICIT", type = "USER_INPUT", status = "DONE", created_at = at,
            content = $"<USER_REQUEST>\n{text}\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nThe current local time is: …\n</ADDITIONAL_METADATA>" });

    private static string Answer(string? text, string tool, string command, string at) =>
        JsonSerializer.Serialize(new { step_index = 1, source = "MODEL", type = "PLANNER_RESPONSE", status = "DONE", created_at = at, content = text,
            tool_calls = new[] { new { name = tool, args = new { CommandLine = command, toolSummary = (string?)null } } } });

    private void History(params (string Text, long Ms, string Folder)[] prompts) =>
        File.WriteAllLines(Path.Combine(home.Path, "history.jsonl"),
            prompts.Select(p => JsonSerializer.Serialize(new { display = p.Text, timestamp = p.Ms, workspace = p.Folder })));

    private static long Ms(string at) => DateTimeOffset.Parse(at).ToUnixTimeMilliseconds();

    [Fact]
    public void Conversations_are_listed_by_the_folder_their_first_prompt_was_typed_in()
    {
        Transcript(A, Prompt("irm https://x.ai/cli/install.ps1 | iex", "2026-11-14T20:17:19Z"));
        Transcript(B, Prompt("fix the tests", "2026-11-14T21:00:00Z"));
        History(("irm https://x.ai/cli/install.ps1 | iex", Ms("2026-11-14T20:17:19.463Z"), work), ("fix the tests", Ms("2026-11-14T21:00:01Z"), @"D:\other"));
        Directory.CreateDirectory(Path.Combine(home.Path, "annotations"));
        File.WriteAllText(Path.Combine(home.Path, "annotations", A + ".pbtxt"), "title:\"Install X.AI CLI \\303\\251 \\\"now\\\"\"\n");

        var here = Assert.Single(AntigravityConversations.List(work, 10));
        Assert.Equal(A, here.SessionId);
        Assert.Equal("Install X.AI CLI é \"now\"", here.Title);
        Assert.Equal("agy", here.Agent);
        Assert.Equal(2, AntigravityConversations.List(null, 10).Count);
        Assert.True(AntigravityConversations.IsSaved(A));
        Assert.False(AntigravityConversations.IsSaved("0f0f0f0f-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void A_session_learns_the_latest_conversation_prompted_in_its_folder()
    {
        var start = DateTime.Parse("2026-11-14T20:00:00Z").ToUniversalTime();
        Transcript(A, Prompt("first", "2026-11-14T19:00:00Z"), Prompt("again", "2026-11-14T20:05:00Z"));   // resumed in the session
        Transcript(B, Prompt("new one", "2026-11-14T20:01:00Z"));
        History(("first", Ms("2026-11-14T19:00:00Z"), work), ("again", Ms("2026-11-14T20:05:00Z"), work), ("new one", Ms("2026-11-14T20:01:00Z"), work));
        Assert.Equal(A, LatestInFolder.Pick(AntigravityConversations.RecentActivity, work, start, new HashSet<string>()));
        Assert.Equal(B, LatestInFolder.Pick(AntigravityConversations.RecentActivity, work, start, new HashSet<string> { A }));
    }

    [Fact]
    public void The_rail_shows_prompts_answers_and_tools()
    {
        Transcript(A, Prompt("install it", "2026-11-14T20:17:19Z"), Answer(null, "run_command", "irm x | iex", "2026-11-14T20:17:20Z"),
            """{"step_index":2,"type":"GENERIC","content":"Command exited with code 1."}""", Answer("It needs the network.", "search_web", "", "2026-11-14T20:17:30Z"),
            "not json", """{"type":"USER_INPUT","content":"<SYSTEM>no request</SYSTEM>"}""");
        var turn = Assert.Single(AntigravityConversations.Read(A)!.Turns);
        Assert.Equal("install it", turn.Prompt);
        Assert.Equal(["tool:irm x | iex", "text:It needs the network.", "tool:"], turn.Items.Select(i => i.Kind + ":" + i.Text));
        Assert.Equal("run_command", turn.Items[0].Tool);
    }
}

/// <summary>Grok's conversations, read from a folder laid out as grok 1.0.77 writes it.</summary>
[Collection(nameof(AgentHomes))]
public sealed class GrokConversationTests : IDisposable
{
    private const string A = "01a1dbb8-b954-7f01-a11d-528b27d9f113";
    private const string B = "01a1dbb8-b954-7f01-a11d-528b27d9f114";
    private readonly TempFolder home = new();
    private readonly string work = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "grok-work"));

    public GrokConversationTests() => GrokConversations.Home = home.Path;

    public void Dispose()
    {
        GrokConversations.Home = null!;
        home.Dispose();
    }

    private string Session(string id, string cwd, string? title, string updated, bool headless = false, params string[] updates)
    {
        var folder = Path.Combine(home.Path, "sessions", Uri.EscapeDataString(cwd), id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "summary.json"), JsonSerializer.Serialize(new
        {
            info = new { id, cwd }, session_summary = "", generated_title = title, created_at = "2026-11-14T20:21:18.9633215Z", updated_at = updated,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt_context.json"), JsonSerializer.Serialize(new { version = 1, is_non_interactive = headless }));
        if (updates.Length > 0) File.WriteAllLines(Path.Combine(folder, "updates.jsonl"), updates);
        return folder;
    }

    private static string Chunk(string kind, string text) => JsonSerializer.Serialize(new { sessionUpdate = kind, content = new { type = "text", text } });

    [Fact]
    public void Only_conversations_with_something_in_them_are_listed_and_never_a_print_run()
    {
        Session(A, work, "Fix login", "2026-11-14T20:30:00Z", false, Chunk("user_message_chunk", "fix login"));
        Session(B, work, null, "2026-11-14T20:40:00Z");                                                          // opened, nothing said
        Session("01a1dbb8-b954-7f01-a11d-528b27d9f115", work, "x", "2026-11-14T20:50:00Z", true, Chunk("user_message_chunk", "hi"));
        var listed = Assert.Single(GrokConversations.List(work, 10));
        Assert.Equal(("Fix login", "grok"), (listed.Title, listed.Agent));
        Assert.True(GrokConversations.IsSaved(B));   // grok saves a conversation as soon as it opens one, so it's resumed rather than created again
    }

    [Fact]
    public void A_session_is_in_the_conversation_grok_names_for_its_process()
    {
        File.WriteAllText(Path.Combine(home.Path, "active_sessions.json"), JsonSerializer.Serialize(new object[]
        {
            new { session_id = A, pid = 100, cwd = work, opened_at = "2026-11-14T20:21:20Z" },
            new { session_id = B, pid = 100, cwd = work, opened_at = "2026-11-14T20:25:00Z" },   // after /new
            new { session_id = "01a1dbb8-b954-7f01-a11d-528b27d9f115", pid = 200, cwd = work, opened_at = "2026-11-14T20:30:00Z" },
        }));
        Assert.Equal(B, GrokConversations.ForProcesses([99, 100]));
        Assert.Null(GrokConversations.ForProcesses([300]));
    }

    [Fact]
    public void The_rail_joins_the_chunks_of_prompts_and_answers()
    {
        Session(A, work, "t", "2026-11-14T20:30:00Z", false,
            Chunk("user_message_chunk", "fix "), Chunk("user_message_chunk", "login"),
            Chunk("agent_thought_chunk", "hmm"),
            JsonSerializer.Serialize(new { update = new { sessionUpdate = "tool_call", title = "read", toolCallId = "c1" } }),
            JsonSerializer.Serialize(new { timestamp = 1794690249, method = "session/update", @params = new { update = new { sessionUpdate = "tool_call_update", toolCallId = "c1", kind = "other", title = "Read src/login.ts" } } }),
            JsonSerializer.Serialize(new { update = new { sessionUpdate = "tool_call_update", toolCallId = "c1", status = "completed" } }),
            Chunk("agent_message_chunk", "Fixed "), Chunk("agent_message_chunk", "it."),
            Chunk("user_message_chunk", "thanks"));
        var turns = GrokConversations.Read(A)!.Turns;
        Assert.Equal(["fix login", "thanks"], turns.Select(t => t.Prompt));
        Assert.Equal(["read:Read src/login.ts", "text:Fixed it."], turns[0].Items.Select(i => (i.Tool ?? i.Kind) + ":" + i.Text));
    }
}
