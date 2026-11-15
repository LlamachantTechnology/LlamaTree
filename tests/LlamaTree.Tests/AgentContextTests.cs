using System.Text.Json;
using LlamaTree.Sessions;

namespace LlamaTree.Tests;

/// <summary>
/// The context meter for every agent: how full a conversation is, read from each agent's own files, with lines shaped
/// as Codex (0.167), agy (1.5.9) and grok (1.0.77) write them.
/// </summary>
public class AgentContextCommandsTests
{
    [Fact]
    public void Every_agent_tells_the_page_its_compact_command()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var agent in Agents.All)
            Assert.Equal("/compact", JsonSerializer.SerializeToElement(agent.Describe(), options).GetProperty("compact").GetString());
    }

    [Theory]
    [InlineData("gemini-3.8-flash-high", 1_048_576)]
    [InlineData("claude-sonnet-4-6", 200_000)]
    [InlineData("gpt-oss-120b-medium", 131_072)]
    [InlineData(null, 200_000)]
    public void Agys_window_is_its_models(string? model, int window) => Assert.Equal(window, AntigravityConversations.WindowOf(model));
}

/// <summary>Codex's rollouts, under a CODEX_HOME of the test's own (so not run beside the other Codex tests).</summary>
[Collection(nameof(GitUndoTests))]
public sealed class CodexContextTests : IDisposable
{
    private const string Id = "01a1dc09-a600-7b90-9eb4-370efed0e575";
    private readonly TempFolder home = new();
    private readonly string? before = Environment.GetEnvironmentVariable("CODEX_HOME");

    public CodexContextTests() => Environment.SetEnvironmentVariable("CODEX_HOME", home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", before);
        home.Dispose();
    }

    private static string Tokens(int input, int output, int window = 258_400) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-11-14T21:51:05.200Z", type = "event_msg",
        payload = new { type = "token_count", info = new
        {
            total_token_usage = new { input_tokens = 999_999, output_tokens = 9_999 },
            last_token_usage = new { input_tokens = input, cached_input_tokens = input / 2, output_tokens = output, total_tokens = input + output },
            model_context_window = window,
        } },
    });

    private void Rollout(params string[] lines)
    {
        var day = Path.Combine(home.Path, "sessions", "2026", "11", "14");
        Directory.CreateDirectory(day);
        var meta = JsonSerializer.Serialize(new { timestamp = "2026-11-14T21:49:42Z", type = "session_meta", payload = new { id = Id, cwd = @"D:\work", source = "cli" } });
        File.WriteAllLines(Path.Combine(day, $"rollout-2026-11-14T16-49-42-{Id}.jsonl"), [meta, .. lines]);
    }

    [Fact]
    public void The_last_reply_against_the_models_window()
    {
        Rollout(JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "gpt-6-astra" } }), Tokens(18_204, 65), Tokens(36_000, 120));
        var c = CodexConversations.Context(Id)!;
        Assert.Equal((36_120, 258_400, 0, "gpt-6-astra"), (c.Tokens, c.Limit, c.Compactions, c.Model));
        Assert.Equal("the model's window", c.LimitIs);
    }

    [Fact]
    public void A_compaction_is_counted_and_the_size_waits_for_the_next_reply()
    {
        Rollout(Tokens(240_000, 1_200),
            JsonSerializer.Serialize(new { timestamp = "2026-11-02T02:14:44.281Z", type = "compacted", payload = new { message = "" } }),
            Tokens(0, 0));   // what Codex writes straight after compacting
        var c = CodexConversations.Context(Id)!;
        Assert.Equal((0, 1), (c.Tokens, c.Compactions));
        Assert.NotNull(c.CompactedAt);
    }
}

/// <summary>agy's transcripts and grok's signals, under homes of the test's own.</summary>
[Collection(nameof(AgentHomes))]
public sealed class NewAgentContextTests : IDisposable
{
    private const string AgyId = "170f5677-72da-4a87-a157-1d4c12aaef46";
    private const string GrokId = "abce693c-bef0-4da7-af71-af4d6e4c289d";
    private readonly TempFolder agy = new(), grok = new();
    private readonly string agyBefore = AntigravityConversations.Home;

    public NewAgentContextTests()
    {
        AntigravityConversations.Home = agy.Path;
        GrokConversations.Home = grok.Path;
    }

    public void Dispose()
    {
        AntigravityConversations.Home = agyBefore;
        GrokConversations.Home = null!;
        agy.Dispose();
        grok.Dispose();
    }

    private static string Step(string type, object extra) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["type"] = type, ["created_at"] = "2026-11-14T20:17:19Z" }
            .Concat(JsonSerializer.SerializeToElement(extra).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value)).ToDictionary());

    [Fact]
    public void Agy_counts_what_each_reply_was_sent_and_wrote()
    {
        var logs = Path.Combine(agy.Path, "brain", AgyId, ".system_generated", "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "transcript_full.jsonl"),
        [
            Step("USER_INPUT", new { content = "<USER_REQUEST>\nhi\n</USER_REQUEST>" }),
            Step("PLANNER_RESPONSE", new { model = "gemini-3.8-flash-high", input_tokens = 9_736, cache_read_tokens = 0, output_tokens = 958 }),
            Step("GENERIC", new { content = "tool output" }),
            Step("PLANNER_RESPONSE", new { model = "gemini-3.8-flash-high", input_tokens = 3_788, cache_read_tokens = 7_704, output_tokens = 356 }),
        ]);
        var c = AntigravityConversations.Context(AgyId)!;
        Assert.Equal((3_788 + 7_704 + 356, 1_048_576, 0), (c.Tokens, c.Limit, c.Compactions));

        File.AppendAllLines(Path.Combine(logs, "transcript_full.jsonl"), [Step("CHECKPOINT", new { content = "summary" })]);
        Assert.Equal((0, 1), (AntigravityConversations.Context(AgyId)!.Tokens, AntigravityConversations.Context(AgyId)!.Compactions));
    }

    private void GrokSession(object signals, string? config = null)
    {
        var folder = Path.Combine(grok.Path, "sessions", "D%3A%5Cwork", GrokId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "summary.json"), JsonSerializer.Serialize(new { info = new { id = GrokId, cwd = @"D:\work" }, created_at = "2026-11-14T21:03:43Z", updated_at = "2026-11-14T21:05:00Z" }));
        File.WriteAllText(Path.Combine(folder, "signals.json"), JsonSerializer.Serialize(signals));
        if (config != null) File.WriteAllText(Path.Combine(grok.Path, "config.toml"), config);
    }

    [Fact]
    public void Grok_is_measured_against_where_it_compacts_on_its_own()
    {
        GrokSession(new { turnCount = 1, compactionCount = 0, contextWindowUsage = 12, contextTokensUsed = 31_331, contextWindowTokens = 256_000, primaryModelId = "grok-4.7" });
        var c = GrokConversations.Context(GrokId)!;
        Assert.Equal((31_331, 217_600, 0, "grok-4.7"), (c.Tokens, c.Limit, c.Compactions, c.Model));   // 85% of 256k
        Assert.Contains("85% of its 256k window", c.LimitIs);
    }

    [Fact]
    public void A_grok_compaction_counts_before_its_signals_are_written_again()
    {
        GrokSession(new { compactionCount = 0, contextTokensUsed = 32_475, contextWindowTokens = 256_000 });
        var folder = Path.Combine(grok.Path, "sessions", "D%3A%5Cwork", GrokId);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "signals.json"), new DateTime(2026, 11, 14, 21, 41, 28, DateTimeKind.Utc));
        File.WriteAllLines(Path.Combine(folder, "updates.jsonl"),
        [
            """{"timestamp":1794695181,"method":"_x.ai/session/update","params":{"sessionId":"x","update":{"sessionUpdate":"compaction_checkpoint","checkpoint_id":"c"}}}""",
            """{"timestamp":1794695181,"method":"_x.ai/session/update","params":{"sessionId":"x","update":{"sessionUpdate":"auto_compact_completed","tokens_before":32680,"tokens_after":21320,"summary_preview":null}}}""",
        ]);
        var c = GrokConversations.Context(GrokId)!;
        Assert.Equal((21_320, 1), (c.Tokens, c.Compactions));
    }

    [Fact]
    public void Grok_follows_its_own_compaction_threshold()
    {
        GrokSession(new { compactionCount = 2, contextTokensUsed = 50_000, contextWindowTokens = 256_000 }, "[session]\nauto_compact_threshold_percent = 70\n");
        var c = GrokConversations.Context(GrokId)!;
        Assert.Equal((179_200, 2), (c.Limit, c.Compactions));
    }
}
