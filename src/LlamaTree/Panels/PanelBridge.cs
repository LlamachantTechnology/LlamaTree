using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlamaTree.Panels;

/// <summary>
/// "LlamaTree mcp-bridge": the panel server over stdio, for an agent that only takes MCP servers from its own config
/// (agy). It's registered there once, with no port or token in it: the agent starts it with the session's environment,
/// which names this run's panel server (<see cref="UrlVariable"/>) and the session's token (<see cref="TokenVariable"/>).
/// Each JSON-RPC message read is posted to the server and its answer written back, several at once (wait_for_reply
/// holds its request open). Started outside a LlamaTree session it offers no tools, so the agent shows no error.
/// </summary>
internal static class PanelBridge
{
    public const string Argument = "mcp-bridge";
    public const string UrlVariable = "LLAMATREE_PANEL_URL";
    public const string TokenVariable = "LLAMATREE_PANEL_TOKEN";

    private const string NotConnected = "This agent isn't running in a LlamaTree session, so LlamaTree's panels and sessions aren't available here.";

    public static int Run()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        return RunAsync(input, output, Environment.GetEnvironmentVariable(UrlVariable), Environment.GetEnvironmentVariable(TokenVariable))
            .GetAwaiter().GetResult();
    }

    /// <summary>How long answers still on their way are waited for once the agent has gone (its input closed).</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    internal static async Task<int> RunAsync(Stream input, Stream output, string? url, string? token, HttpMessageHandler? handler = null, TimeSpan? grace = null)
    {
        using var gone = new CancellationTokenSource();
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { NewLine = "\n" };
        using var writing = new SemaphoreSlim(1, 1);
        // Never sent anywhere but this machine's panel server, whatever the environment says.
        token = token?.Trim();
        using var http = IsPanelServer(url) && !string.IsNullOrEmpty(token) && !token.Any(char.IsControl)
            ? new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }
            : null;
        var pending = new List<Task>();

        async Task AnswerAsync(string line)
        {
            try
            {
                var reply = http != null ? await ForwardAsync(http, url!, token!, line, gone.Token) : Offline(line, NotConnected);
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

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            pending.RemoveAll(t => t.IsCompleted);
            pending.Add(Task.Run(() => AnswerAsync(line)));
        }
        // A wait_for_reply can take minutes, and the bridge holds LlamaTree's program open while it runs.
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

    /// <summary>The server's answer (one line of JSON), or null for a notification.</summary>
    private static async Task<string?> ForwardAsync(HttpClient http, string url, string token, string line, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(line, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Accepted) return null;
            if (response.IsSuccessStatusCode)
            {
                var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
                if (body.Length == 0) return null;
                return body.Contains('\n') ? JsonNode.Parse(body)?.ToJsonString() : body;   // one message, one line
            }
            return Offline(line, response.StatusCode == HttpStatusCode.Unauthorized
                ? "LlamaTree no longer knows this session (it was removed, or LlamaTree restarted): restart the session to reconnect."
                : $"LlamaTree's panel server answered {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return Offline(line, "LlamaTree isn't running any more, so its panels and sessions aren't available: restart the session from LlamaTree.");
        }
    }

    /// <summary>
    /// The answer when there's no panel server to ask: a server with no tools, so the agent starts cleanly, and
    /// <paramref name="why"/> for anything else. Null for a notification, or a line that isn't a request.
    /// </summary>
    internal static string? Offline(string line, string why)
    {
        JsonNode? message;
        try { message = JsonNode.Parse(line); }
        catch (JsonException) { return PanelMcp.Error(null, -32700, "Parse error").ToJsonString(); }
        if (message is not JsonObject o || !o.TryGetPropertyValue("id", out var id) || o["method"] is not JsonValue m || !m.TryGetValue<string>(out var method))
            return null;
        id = id?.DeepClone();
        JsonObject result;
        switch (method)
        {
            case "initialize":
                result = new JsonObject
                {
                    ["protocolVersion"] = o["params"]?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = PanelMcp.ServerName, ["version"] = Updates.UpdateService.CurrentVersion.ToString() },
                    ["instructions"] = why,
                };
                break;
            case "tools/list":
                result = new JsonObject { ["tools"] = new JsonArray() };
                break;
            case "ping":
                result = new JsonObject();
                break;
            default:
                return PanelMcp.Error(id, -32000, why).ToJsonString();
        }
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
    }
}
