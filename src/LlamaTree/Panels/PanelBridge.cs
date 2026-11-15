using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using LlamaTree.Sessions;

namespace LlamaTree.Panels;

/// <summary>
/// "LlamaTree mcp-bridge": the panel server as a stdio MCP server, for an agent that only runs MCP servers registered in
/// its own settings (see <see cref="PanelConnection.Registered"/>). It's registered once, with no address or token in
/// it: the agent starts it with the session's environment, which names this run's panel server and the session's token
/// (<see cref="PanelConnection.UrlVariable"/>, <see cref="PanelConnection.TokenVariable"/>). Each message read is posted
/// to the server and its answer written back, several at once (wait_for_reply holds its request open). Started outside
/// a LlamaTree session it offers no tools, so the agent starts cleanly.
/// </summary>
internal static class PanelBridge
{
    public const string Argument = "mcp-bridge";

    /// <summary>How long answers still on their way are waited for once the agent has closed its end.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private const string NotInSession = "This agent isn't running in a LlamaTree session, so LlamaTree's panels and sessions aren't available here.";

    public static int Run()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        return RunAsync(input, output, Environment.GetEnvironmentVariable(PanelConnection.UrlVariable),
            Environment.GetEnvironmentVariable(PanelConnection.TokenVariable)).GetAwaiter().GetResult();
    }

    internal static async Task<int> RunAsync(Stream input, Stream output, string? url, string? token, HttpMessageHandler? handler = null, TimeSpan? grace = null)
    {
        token = token?.Trim();
        // Only ever this machine's panel server, whatever the environment says, and only with a token that can be a header.
        var connected = IsPanelServer(url) && !string.IsNullOrEmpty(token) && !token.Any(char.IsControl);
        using var http = connected
            ? new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }
            : null;
        using var gone = new CancellationTokenSource();
        using var writing = new SemaphoreSlim(1, 1);
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { NewLine = "\n" };
        var pending = new List<Task>();

        async Task AnswerAsync(string line)
        {
            try
            {
                var reply = http != null ? await ForwardAsync(http, url!, token!, line, gone.Token) : Offline(line, NotInSession);
                if (reply == null) return;
                await writing.WaitAsync(gone.Token);
                try
                {
                    await writer.WriteLineAsync(reply);
                    await writer.FlushAsync();
                }
                finally { writing.Release(); }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }   // the agent has gone
        }

        using (var reader = new StreamReader(input, new UTF8Encoding(false)))
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Trim().Length == 0) continue;
                pending.RemoveAll(t => t.IsCompleted);
                pending.Add(Task.Run(() => AnswerAsync(line)));
            }
        var all = Task.WhenAll(pending);
        await Task.WhenAny(all, Task.Delay(grace ?? Grace));
        gone.Cancel();
        await all;
        try { await writer.DisposeAsync(); }
        catch (IOException) { }
        return 0;
    }

    /// <summary>http://127.0.0.1:&lt;port&gt;/mcp, as <see cref="PanelServer.Url"/> is.</summary>
    internal static bool IsPanelServer(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1" && uri.AbsolutePath == "/mcp";

    /// <summary>The server's answer as one line, or null for a notification (202).</summary>
    private static async Task<string?> ForwardAsync(HttpClient http, string url, string token, string line, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(line, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Accepted) return null;
            if (!response.IsSuccessStatusCode)
                return Offline(line, response.StatusCode == HttpStatusCode.Unauthorized
                    ? "LlamaTree no longer knows this session (it was removed, or LlamaTree restarted): restart the session to reconnect."
                    : $"LlamaTree's panel server answered {(int)response.StatusCode}.");
            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (body.Length == 0) return null;
            return body.Contains('\n') ? JsonNode.Parse(body)?.ToJsonString() : body;   // one message, one line
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return Offline(line, "LlamaTree isn't running any more, so its panels and sessions aren't available: restart the session from LlamaTree.");
        }
    }

    /// <summary>
    /// The answer with no panel server to ask: a server with no tools (so the agent starts cleanly) whose instructions say
    /// <paramref name="why"/>, and that as the error for anything else. Null for a notification, or a line that isn't a request.
    /// </summary>
    internal static string? Offline(string line, string why)
    {
        JsonNode? message;
        try { message = JsonNode.Parse(line); }
        catch (System.Text.Json.JsonException) { return PanelMcp.Error(null, -32700, "Parse error").ToJsonString(); }
        if (message is not JsonObject o || !o.TryGetPropertyValue("id", out var id) || o["method"] is not JsonValue m || !m.TryGetValue<string>(out var method))
            return null;
        id = id?.DeepClone();
        JsonObject? result = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = o["params"]?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                ["serverInfo"] = new JsonObject { ["name"] = PanelMcp.ServerName, ["version"] = Updates.UpdateService.CurrentVersion.ToString() },
                ["instructions"] = why,
            },
            "tools/list" => new JsonObject { ["tools"] = new JsonArray() },
            "ping" => [],
            _ => null,
        };
        return result == null
            ? PanelMcp.Error(id, -32000, why).ToJsonString()
            : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
    }
}
