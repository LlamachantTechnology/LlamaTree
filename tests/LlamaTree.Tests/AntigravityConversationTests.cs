using System.Text.Json;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>Reading agy's saved conversations. Points <see cref="AntigravityConversations.Home"/> elsewhere for the whole process, so not run in parallel.</summary>
[Collection(nameof(GitUndoTests))]
public sealed class AntigravityConversationTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "ct-agy-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string work = Path.Combine(Path.GetTempPath(), "ct-agy-work-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string previousHome = AntigravityConversations.Home;

    public AntigravityConversationTests()
    {
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(work);
        AntigravityConversations.Home = home;
    }

    public void Dispose()
    {
        AntigravityConversations.Home = previousHome;
        foreach (var dir in new[] { home, work })
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Line(object o) => JsonSerializer.Serialize(o) + "\n";

    private static string NewId() => Guid.NewGuid().ToString();

    /// <summary>A prompt as agy wraps it in a transcript.</summary>
    private static string Wrapped(string prompt) =>
        $"<USER_REQUEST>\n{prompt}\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nThe current local time is: now.\n</ADDITIONAL_METADATA>";

    /// <summary>A USER_INPUT step; agy writes its time in whole seconds.</summary>
    private static string UserInput(string prompt, DateTime utc, int step = 0) =>
        Line(new { step_index = step, source = "USER_EXPLICIT", type = "USER_INPUT", status = "DONE", created_at = utc.ToString("yyyy-MM-ddTHH:mm:ssZ"), content = Wrapped(prompt) });

    private static string Step(string type, object fields, string source = "MODEL")
    {
        var o = JsonSerializer.SerializeToNode(fields)!.AsObject();
        o["type"] = type;
        o["source"] = source;
        o["created_at"] = "2026-10-29T19:22:03Z";
        return o.ToJsonString() + "\n";
    }

    /// <summary>
    /// A conversation as agy saves it: the transcript starting with its first prompt, and that prompt in history.jsonl
    /// (with no conversation id, as agy records a new conversation's first) unless <paramref name="inHistory"/> is false (agy -p).
    /// </summary>
    private string Conversation(string id, DateTime startedUtc, string workspace, string prompt, bool inHistory = true)
    {
        var logs = Path.Combine(home, "brain", id, ".system_generated", "logs");
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "transcript.jsonl");
        File.WriteAllText(path, UserInput(prompt, startedUtc));
        if (inHistory) Prompted(null, startedUtc.AddMilliseconds(400), workspace, prompt);
        return path;
    }

    /// <summary>A prompt as agy records it in history.jsonl.</summary>
    private void Prompted(string? id, DateTime atUtc, string workspace, string text = "go on")
    {
        var ms = new DateTimeOffset(atUtc).ToUnixTimeMilliseconds();
        File.AppendAllText(Path.Combine(home, "history.jsonl"), id == null
            ? Line(new { display = text, timestamp = ms, workspace })
            : Line(new { display = text, timestamp = ms, workspace, conversationId = id }));
    }

    private void Titled(string id, string pbtxt)
    {
        Directory.CreateDirectory(Path.Combine(home, "annotations"));
        File.WriteAllText(Path.Combine(home, "annotations", id + ".pbtxt"), pbtxt);
    }

    [Fact]
    public void Lists_a_folders_conversations_with_their_first_prompt_and_title()
    {
        var a = NewId();
        var b = NewId();
        var now = DateTime.UtcNow;
        Conversation(a, now.AddMinutes(-5), work, "Add a login page");
        Conversation(b, now.AddMinutes(-4), Path.GetTempPath(), "Elsewhere");
        Conversation(NewId(), now.AddMinutes(-3), work, "printed", inHistory: false);   // agy -p
        File.AppendAllText(Path.Combine(home, "history.jsonl"), Line(new { display = "/mcp", timestamp = 1, workspace = work, type = "slash_command" }));
        Titled(a, "title:\"Login page \\303\\251t\\303\\251\"");

        var c = Assert.Single(AntigravityConversations.List(work, 10));

        Assert.Equal((a, "Login page été", "Add a login page", "agy", work), (c.SessionId, c.Title, c.FirstPrompt, c.Agent, c.Cwd));
        Assert.True(AntigravityConversations.IsSaved(a));
        Assert.False(AntigravityConversations.IsSaved(NewId()));
        Assert.Equal("Elsewhere", AntigravityConversations.Find(b)?.Title);
        Assert.Contains(AntigravityConversations.List(null, 10), x => x.SessionId == b);
    }

    [Fact]
    public void A_conversation_a_prompt_was_sent_in_is_listed_in_that_prompts_folder()
    {
        // Its first prompt isn't matched in history.jsonl, but a later one names the conversation.
        var id = NewId();
        var started = DateTime.UtcNow.AddMinutes(-5);
        Conversation(id, started, work, "first", inHistory: false);
        Prompted(id, started.AddMinutes(1), work);

        Assert.Equal(id, Assert.Single(AntigravityConversations.List(work, 10)).SessionId);
    }

    [Fact]
    public void A_session_learns_the_conversation_its_agy_started()
    {
        var started = DateTime.UtcNow.AddMinutes(-1);
        Conversation(NewId(), started.AddMinutes(-10), work, "before the session");
        Conversation(NewId(), started.AddSeconds(5), Path.GetTempPath(), "another folder");
        Conversation(NewId(), started.AddSeconds(6), work, "printed", inHistory: false);   // agy -p in the same folder
        var mine = NewId();
        Conversation(mine, started.AddSeconds(4), work, "hello");

        Assert.Equal(mine, AntigravityConversations.Live(work, started, new HashSet<string>()));
        Assert.Null(AntigravityConversations.Live(work, started, new HashSet<string> { mine }));   // another session is in it

        var next = NewId();   // a new conversation, then a prompt
        Conversation(next, started.AddSeconds(30), work, "again");
        Assert.Equal(next, AntigravityConversations.Live(work, started, new HashSet<string>()));
    }

    [Fact]
    public void A_session_follows_agy_to_a_conversation_picked_with_resume()
    {
        var started = DateTime.UtcNow.AddMinutes(-2);
        var old = NewId();
        Conversation(old, started.AddDays(-3), work, "last week's work");
        var elsewhere = NewId();
        Conversation(elsewhere, started.AddDays(-3), Path.GetTempPath(), "another folder");
        var mine = NewId();
        Conversation(mine, started.AddSeconds(5), work, "hello");
        Prompted(old, started.AddMinutes(-30), work);   // before the session: not this session's doing
        Assert.Equal(mine, AntigravityConversations.Live(work, started, new HashSet<string>()));

        Prompted(old, started.AddSeconds(40), work);   // /resume (or -c), then a prompt
        Assert.Equal(old, AntigravityConversations.Live(work, started, new HashSet<string>()));
        Assert.Equal(mine, AntigravityConversations.Live(work, started, new HashSet<string> { old }));   // another session is in it

        Prompted(elsewhere, started.AddSeconds(50), Path.GetTempPath());   // an agy in another folder
        Assert.Equal(old, AntigravityConversations.Live(work, started, new HashSet<string>()));

        var next = NewId();   // a new conversation after that
        Conversation(next, started.AddSeconds(60), work, "fresh start");
        Assert.Equal(next, AntigravityConversations.Live(work, started, new HashSet<string>()));
    }

    [Fact]
    public void The_rail_reads_an_agy_conversation_as_prompts_and_answers()
    {
        // Shapes as agy writes them (see a transcript in ~/.gemini/antigravity-cli/brain).
        var id = NewId();
        var path = Conversation(id, DateTime.UtcNow.AddMinutes(-1), work, "Fix the login bug");
        File.AppendAllText(path,
            Step("PLANNER_RESPONSE", new { thinking = "Let me look.", tool_calls = new object[] { new { name = "read_file", args = new { Path = "src/Login.cs" } } } }) +
            Step("GENERIC", new { content = "the file's text" }) +
            Step("PLANNER_RESPONSE", new
            {
                content = "I'll run the tests.",
                tool_calls = new object[]
                {
                    new { name = "run_command", args = new { CommandLine = "dotnet test", WaitMsBeforeAsync = 500, toolSummary = "Running tests" } },
                    new { name = "call_mcp_tool", args = new { ServerName = "llamatree", ToolName = "show_panel", Arguments = new { } } },
                    new { name = "ask_question", args = new { questions = new[] { "Which?" } } },
                },
            }) +
            Step("SYSTEM_MESSAGE", new { content = "The following is a <SYSTEM_MESSAGE> not actually sent by the user." }, "SYSTEM") +
            Step("PLANNER_RESPONSE", new { content = "Fixed: the token was never refreshed." }) +
            UserInput("Thanks", new DateTime(2026, 10, 29, 19, 30, 0, DateTimeKind.Utc), 9));

        var c = AntigravityConversations.Read(id);

        Assert.NotNull(c);
        Assert.Equal(id, c.SessionId);
        Assert.Equal(["Fix the login bug", "Thanks"], c.Turns.Select(t => t.Prompt));
        Assert.Equal(
            [("tool", "src/Login.cs", "read_file"), ("text", "I'll run the tests.", (string?)null), ("tool", "dotnet test", "run_command"),
             ("tool", "llamatree/show_panel", "call_mcp_tool"), ("tool", "", "ask_question"), ("text", "Fixed: the token was never refreshed.", null)],
            c.Turns[0].Items.Select(i => (i.Kind, i.Text, i.Tool)));
        Assert.Equal(new DateTime(2026, 10, 29, 19, 30, 0, DateTimeKind.Utc), c.Turns[1].Time?.ToUniversalTime());

        // Only what agy adds since is read next time, and the version tells the page something changed.
        File.AppendAllText(path, Step("PLANNER_RESPONSE", new { content = "You're welcome." }));
        var again = AntigravityConversations.Read(id);
        Assert.NotEqual(c.Version, again!.Version);
        Assert.Equal("You're welcome.", Assert.Single(again.Turns[1].Items).Text);
    }

    [Fact]
    public void The_full_transcript_is_read_when_agy_wrote_one()
    {
        var id = NewId();
        var path = Conversation(id, DateTime.UtcNow.AddMinutes(-1), work, "Explain it");
        File.AppendAllText(path, Step("PLANNER_RESPONSE", new { content = "cut…", truncated_fields = new[] { "content" } }));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "transcript_full.jsonl"),
            UserInput("Explain it", DateTime.UtcNow.AddMinutes(-1)) + Step("PLANNER_RESPONSE", new { content = "the whole answer" }));

        Assert.Equal("the whole answer", Assert.Single(Assert.Single(AntigravityConversations.Read(id)!.Turns).Items).Text);
    }

    [Fact]
    public void Lines_with_fields_of_unexpected_types_are_skipped()
    {
        var id = NewId();
        var path = Conversation(id, DateTime.UtcNow.AddMinutes(-1), work, "Fix the login bug");
        File.AppendAllText(path,
            """{"type":"PLANNER_RESPONSE","content":7,"tool_calls":[3,null,{"name":5,"args":"oops"}]}""" + "\n" +
            """{"type":"USER_INPUT","content":{"an":"object"}}""" + "\n" +
            """{"type":"USER_INPUT","content":"<USER_REQUEST>  </USER_REQUEST>"}""" + "\n" +
            "[1]\nnull\n" +
            Step("PLANNER_RESPONSE", new { content = "Done." }));
        File.AppendAllText(Path.Combine(home, "history.jsonl"),
            "not json\n[1]\n" + """{"display":"x","timestamp":"soon","workspace":"C:\\w"}""" + "\n" + """{"display":"x","timestamp":1e30,"workspace":"C:\\w"}""" + "\n");

        for (var poll = 0; poll < 3; poll++)
        {
            var c = AntigravityConversations.Read(id);
            Assert.NotNull(c);
            Assert.Equal("Fix the login bug", Assert.Single(c.Turns).Prompt);
            Assert.Equal([("tool", "", "tool"), ("text", "Done.", (string?)null)], c.Turns[0].Items.Select(i => (i.Kind, i.Text, i.Tool)));
        }
        Assert.Single(AntigravityConversations.List(work, 10));
    }

    [Theory]
    [InlineData(@"plain", "plain")]
    [InlineData(@"say \""hi\"" \\ \n", "say \"hi\" \\ \n")]
    [InlineData(@"caf\303\251 \xc3\xa9", "café é")]
    [InlineData("emoji 😀", "emoji 😀")]
    public void Titles_are_unescaped_as_protobuf_text(string escaped, string expected) =>
        Assert.Equal(expected, AntigravityConversations.Unescape(escaped));

    [Fact]
    public void An_agy_session_shows_its_own_conversation()
    {
        var id = NewId();
        Conversation(id, DateTime.UtcNow.AddMinutes(-1), work, "hello");
        using var s = new TerminalSession(new SessionInfo { Command = "agy", Directory = work, Conversation = id });

        Assert.True(s.Agent!.ShowsConversation);
        Assert.Equal("hello", Assert.Single(s.Agent.ReadConversation(s, [])!.Turns).Prompt);
        Assert.Equal("hello", Assert.Single(s.Agent.ReadSavedConversation(s)!.Turns).Prompt);

        using var fresh = new TerminalSession(new SessionInfo { Command = "agy", Directory = work });   // nothing learned yet
        Assert.Null(fresh.Agent!.ReadConversation(fresh, []));
    }
}
