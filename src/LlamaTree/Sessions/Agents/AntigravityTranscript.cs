using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaTree.Sessions;

/// <summary>
/// Reads an agy transcript into prompts and answers for the conversation rail and Conversation tab (see
/// <see cref="ConversationReader.Read"/>). Each line is one step: USER_INPUT is a prompt, wrapped by agy in tags with the
/// person's text in &lt;USER_REQUEST&gt;; PLANNER_RESPONSE is the model's turn, with its text in content and the tools it
/// called in tool_calls. Tool results (GENERIC), messages from the system (SYSTEM_MESSAGE, which is how messages from
/// other sessions arrive) and summaries (CHECKPOINT) aren't shown.
/// </summary>
internal static partial class AntigravityTranscript
{
    internal const string UserInput = "USER_INPUT";

    /// <summary>One transcript line (a <see cref="TranscriptParser"/>).</summary>
    public static void Line(JsonElement e, List<ConversationTurn> turns)
    {
        switch (Str(e, "type"))
        {
            case UserInput:
                if (Str(e, "content") is { } content && PromptText(content) is { } prompt)
                    turns.Add(new ConversationTurn
                    {
                        Index = turns.Count,
                        Prompt = ConversationReader.Clip(prompt, ConversationReader.MaxTextChars),
                        Time = ConversationReader.Time(e, "created_at"),
                    });
                break;
            case "PLANNER_RESPONSE" when turns.Count > 0:
                if (Str(e, "content") is { } text && !string.IsNullOrWhiteSpace(text))
                    turns[^1].Items.Add(new ConversationItem("text", ConversationReader.Clip(text.Trim(), ConversationReader.MaxTextChars)));
                if (e.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    foreach (var call in calls.EnumerateArray())
                        if (call.ValueKind == JsonValueKind.Object)
                            turns[^1].Items.Add(Tool(Str(call, "name"), ConversationReader.Obj(call, "args", out var args) ? Detail(args) : null));
                break;
        }
    }

    /// <summary>What the person typed: the &lt;USER_REQUEST&gt; of a prompt agy wrapped, or the whole of one it didn't; null for none.</summary>
    internal static string? PromptText(string content)
    {
        var m = UserRequest().Match(content);
        var text = (m.Success ? m.Groups[1].Value : content).Trim();
        return text.Length == 0 || (!m.Success && text.StartsWith('<')) ? null : text;
    }

    [GeneratedRegex(@"<USER_REQUEST>(.*?)</USER_REQUEST>", RegexOptions.Singleline)]
    private static partial Regex UserRequest();

    private static ConversationItem Tool(string? name, string? detail) =>
        new("tool", ConversationReader.Clip((detail ?? "").ReplaceLineEndings(" ").Trim(), ConversationReader.MaxToolChars), name ?? "tool");

    /// <summary>The telling arguments of agy's tools (run_command, read_file, edit_file, write_file, search_web, call_mcp_tool, …).</summary>
    private static readonly string[] ArgumentKeys = ["CommandLine", "Path", "AbsolutePath", "TargetFile", "Url", "query", "Query", "ToolName", "SearchPath", "Pattern"];

    private static string? Detail(JsonElement args)
    {
        foreach (var key in ArgumentKeys)
            if (Str(args, key) is { Length: > 0 } value)
                return key == "ToolName" && Str(args, "ServerName") is { Length: > 0 } server ? $"{server}/{value}" : value;
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
