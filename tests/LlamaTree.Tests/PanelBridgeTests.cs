using System.Net;
using System.Text;
using System.Text.Json;
using LlamaTree.Panels;

namespace LlamaTree.Tests;

/// <summary>The stdio bridge an agent like agy starts: relaying to the panel server named in its environment.</summary>
public class PanelBridgeTests
{
    private const string Url = "http://127.0.0.1:5000/mcp";

    private static async Task<List<JsonElement>> Run(string input, string? url, string? token, HttpMessageHandler? handler = null)
    {
        using var output = new MemoryStream();
        await PanelBridge.RunAsync(new MemoryStream(Encoding.UTF8.GetBytes(input)), output, url, token, handler);
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private sealed class Server(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return answer(request, await request.Content!.ReadAsStringAsync(ct));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Each_message_is_posted_with_the_token_and_its_answer_written_back()
    {
        var server = new Server((_, body) => body.Contains("\"id\"")
            ? Json("{\n\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"tools\":[]}\n}")
            : new HttpResponseMessage(HttpStatusCode.Accepted));
        var replies = await Run("""
            {"jsonrpc":"2.0","id":1,"method":"tools/list"}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            """, Url, "tok", server);

        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.Equal("Bearer tok", r.Headers.Authorization!.ToString()));
        Assert.All(server.Requests, r => Assert.Equal(Url, r.RequestUri!.ToString()));
        var reply = Assert.Single(replies);   // the notification gets no answer; the answer is one line
        Assert.Equal(1, reply.GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(Url, null)]
    [InlineData(Url, "to\nk")]   // a token that can't go in a header
    [InlineData("https://example.com/mcp", "tok")]   // only ever this machine's panel server
    [InlineData("http://10.0.0.5:5000/mcp", "tok")]
    public async Task Outside_a_session_it_is_a_server_with_no_tools(string? url, string? token)
    {
        var server = new Server((_, _) => throw new InvalidOperationException("must not be called"));
        var replies = await Run("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"show_panel"}}
            """, url, token, server);

        Assert.Empty(server.Requests);
        var byId = replies.ToDictionary(r => r.GetProperty("id").GetInt32());
        Assert.Equal("2025-06-18", byId[1].GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("llamatree", byId[1].GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(0, byId[2].GetProperty("result").GetProperty("tools").GetArrayLength());
        Assert.True(byId[3].TryGetProperty("error", out _));
    }

    [Fact]
    public async Task When_LlamaTree_has_gone_the_agent_still_starts()
    {
        var server = new Server((_, _) => throw new HttpRequestException("refused"));
        var replies = await Run("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"show_panel"}}
            """, Url, "tok", server);

        var byId = replies.ToDictionary(r => r.GetProperty("id").GetInt32());
        Assert.True(byId[1].TryGetProperty("result", out _));
        Assert.Contains("isn't running", byId[2].GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_forgotten_token_says_to_restart_the_session()
    {
        var server = new Server((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var reply = Assert.Single(await Run("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"list_panels"}}""", Url, "old", server));
        Assert.Contains("restart the session", reply.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_slow_call_doesnt_hold_up_the_next()
    {
        var release = new TaskCompletionSource();
        var server = new Server((_, body) =>
        {
            if (body.Contains("wait_for_reply")) release.Task.Wait(TimeSpan.FromSeconds(10));
            else release.TrySetResult();
            var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32();
            return Json($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{}}""");
        });
        var replies = await Run("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"wait_for_reply"}}
            {"jsonrpc":"2.0","id":2,"method":"ping"}
            """, Url, "tok", server);

        Assert.Equal([2, 1], replies.Select(r => r.GetProperty("id").GetInt32()));
    }

    private sealed class Endless : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task Once_the_agent_has_gone_a_long_wait_is_given_up()
    {
        using var output = new MemoryStream();
        var run = PanelBridge.RunAsync(new MemoryStream(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"wait_for_reply"}}""")),
            output, Url, "tok", new Endless(), TimeSpan.FromMilliseconds(100));
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal(0, await run);
        Assert.Empty(output.ToArray());   // no one to answer
    }
}
