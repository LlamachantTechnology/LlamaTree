using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LlamaTree.Sessions;

/// <summary>
/// Where an agent started by a session serves its own API on this computer (OpenCode's --port), and the password it asks
/// for (OPENCODE_SERVER_PASSWORD), so only the app can use it.
/// </summary>
internal sealed record AgentServer(int Port, string Password)
{
    /// <summary>A server on a free port of this computer's own address, with a new password.</summary>
    public static AgentServer OnFreePort() => new(FreePort(), RandomNumberGenerator.GetHexString(32, lowercase: true));

    /// <summary>A port nothing listens on now. Another program could take it before the agent does; then the agent says so.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

/// <summary>
/// OpenCode's HTTP API, as its interactive interface serves it for one session (opencode --port, checked with 1.19.6):
/// GET /session lists conversations ("sessions"), /session/{id}/message one's messages, /config/providers the models'
/// limits, and /event is a stream of server-sent events. Every request carries the session's password.
/// </summary>
internal static class OpenCodeApi
{
    /// <summary>The user name OpenCode expects with its password.</summary>
    private const string User = "opencode";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) })
    {
        Timeout = Timeout.InfiniteTimeSpan,   // each request has its own; the event stream has none
    };

    /// <summary>The body of GET <paramref name="path"/> (with its query), or null when the server can't be reached or refuses.</summary>
    public static byte[]? Get(AgentServer server, string path)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            using var response = Http.Send(Request(server, path), timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var body = response.Content.ReadAsStream(timeout.Token);
            using var copy = new MemoryStream();
            body.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or SocketException)
        {
            return null;
        }
    }

    /// <summary>GET <paramref name="path"/> as JSON, or null.</summary>
    public static JsonDocument? GetJson(AgentServer server, string path)
    {
        if (Get(server, path) is not { } body) return null;
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }

    /// <summary>Opens the server's event stream; throws when it can't be reached.</summary>
    public static async Task<Stream> OpenEvents(AgentServer server, CancellationToken ct)
    {
        var request = Request(server, "/event");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"OpenCode's server answered {(int)response.StatusCode}.");
        }
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>The query value for a folder.</summary>
    public static string Query(string value) => Uri.EscapeDataString(value);

    private static HttpRequestMessage Request(AgentServer server, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{server.Password}")));
        return request;
    }
}

/// <summary>
/// Follows a running OpenCode through its event stream: whether it's working, waiting on your answer (a permission or a
/// question), or done; and which of its conversations it last worked in, which is the session's.
/// </summary>
internal sealed class OpenCodeWatch
{
    /// <summary>The servers being followed, so their conversations can be read while their sessions run.</summary>
    private static readonly ConcurrentDictionary<AgentServer, OpenCodeWatch> Watches = new();

    private readonly object gate = new();
    private readonly HashSet<string> busy = new(StringComparer.Ordinal);
    private readonly HashSet<string> asks = new(StringComparer.Ordinal);
    private readonly HashSet<string> children = new(StringComparer.Ordinal);
    private string? reported;

    /// <summary>The conversation it last started working in (not a sub-agent's), or null while it hasn't.</summary>
    public string? Conversation { get; private set; }

    /// <summary>The servers of the running OpenCode sessions.</summary>
    public static IEnumerable<AgentServer> Servers => Watches.Keys;

    /// <summary>The watch of <paramref name="server"/>, while it runs.</summary>
    public static OpenCodeWatch? Of(AgentServer? server) => server != null && Watches.TryGetValue(server, out var w) ? w : null;

    /// <summary>
    /// Follows <paramref name="server"/> while <paramref name="running"/> says its session runs, calling
    /// <paramref name="report"/> with "working", "blocked", "done" or "idle" (as an OSC 7501 state) whenever that changes.
    /// It waits for the server to come up, and connects again if the stream drops.
    /// </summary>
    public static async Task Follow(AgentServer server, Func<bool> running, Action<string> report)
    {
        var watch = new OpenCodeWatch();
        Watches[server] = watch;
        try
        {
            while (running())
            {
                try
                {
                    using var stop = new CancellationTokenSource();
                    await using var stream = await OpenCodeApi.OpenEvents(server, stop.Token);
                    watch.Seed(server);
                    watch.Report(report);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    // A stream with nothing to say still sends a heartbeat, so this notices the session stopping.
                    using var check = new System.Threading.Timer(_ =>
                    {
                        // A tick already under way as the timer is disposed can come after the source is gone.
                        try { if (!running()) stop.Cancel(); }
                        catch (ObjectDisposedException) { }
                    }, null, 2000, 2000);
                    while (await reader.ReadLineAsync(stop.Token) is { } line)
                    {
                        if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                        if (watch.Apply(line[5..].Trim())) watch.Report(report);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or SocketException) { }
                if (running()) await Task.Delay(1000);   // starting up, or restarting its server
            }
        }
        finally
        {
            Watches.TryRemove(new KeyValuePair<AgentServer, OpenCodeWatch>(server, watch));
        }
    }

    /// <summary>What's true as it connects: conversations working now, and permissions and questions waiting.</summary>
    private void Seed(AgentServer server)
    {
        lock (gate)
        {
            busy.Clear();
            asks.Clear();
            using (var status = OpenCodeApi.GetJson(server, "/session/status"))
                if (status?.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in status.RootElement.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Object && IsBusy(JsonLines.Str(p.Value, "type"))) busy.Add(p.Name);
            foreach (var path in new[] { "/permission", "/question" })
            {
                using var pending = OpenCodeApi.GetJson(server, path);
                if (pending?.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var e in pending.RootElement.EnumerateArray())
                        if (JsonLines.Str(e, "id") is { } id) asks.Add(id);
            }
        }
    }

    /// <summary>Takes in one event (its JSON); true when it may have changed the state.</summary>
    internal bool Apply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) return false;
            lock (gate)
            {
                switch (JsonLines.Str(e, "type"))
                {
                    case "session.status" when JsonLines.Str(p, "sessionID") is { } id && p.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object:
                        if (!IsBusy(JsonLines.Str(status, "type"))) return busy.Remove(id);
                        if (!children.Contains(id)) Conversation = id;
                        return busy.Add(id);
                    case "session.idle" when JsonLines.Str(p, "sessionID") is { } id:
                        return busy.Remove(id);
                    case "session.created" or "session.updated" when p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object:
                        // A sub-agent's conversation has a parent: its work isn't the session's conversation.
                        if (JsonLines.Str(info, "parentID") is { Length: > 0 } && JsonLines.Str(info, "id") is { } child)
                        {
                            children.Add(child);
                            if (Conversation == child) Conversation = null;
                        }
                        return false;
                    case "permission.asked" or "question.asked" when JsonLines.Str(p, "id") is { } ask:
                        return asks.Add(ask);
                    case "permission.replied" or "question.replied" or "question.rejected" when JsonLines.Str(p, "requestID") is { } answered:
                        return asks.Remove(answered);
                    default:
                        return false;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The state now, as an OSC 7501 state: blocked on you, working, done after working, or idle.</summary>
    internal string State
    {
        get
        {
            lock (gate)
                return asks.Count > 0 ? "blocked" : busy.Count > 0 ? "working" : reported is "working" or "blocked" or "done" ? "done" : "idle";
        }
    }

    private void Report(Action<string> report)
    {
        var now = State;
        if (now == reported) return;
        reported = now;
        report(now);
    }

    /// <summary>"busy", or "retry" (waiting to try the model again), is working.</summary>
    private static bool IsBusy(string? type) => type is "busy" or "retry";
}
