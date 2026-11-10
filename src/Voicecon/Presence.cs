using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Voicecon;

public enum PresenceStatus { Unknown, Offline, Online, InCall }
public sealed record PresenceInfo(PresenceStatus Status, DateTimeOffset? LastSeen);

/// <summary>
/// Friend online status over the same ntfy service as call delivery.
/// Each client publishes one small message to its own presence topic (derived from its private inbox name, which only
/// friends know): on start, every 20 minutes, on call start/end and on exit. Friends subscribe to those topics and
/// read the latest cached message on connect. Costs about 75 publications a day regardless of the number of friends.
/// Messages carry only a state and a timestamp — never names, keys or addresses.
/// </summary>
public sealed class PresenceService : IDisposable
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(45);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly Dictionary<string, (string Server, string Topic)> watched = []; // friend id -> topic
    private readonly Dictionary<string, (string State, long Time)> latest = [];       // friend id -> last message
    private CancellationTokenSource? subscription;
    private FriendCard own;
    private string ownState = "on";
    private bool visible = true;
    private long lastPublish;
    private string lastPublished = "";
    private Task? heartbeat;
    public event Action<string>? Changed;

    public PresenceService(FriendCard own) { this.own = own; }

    public static string TopicFor(string inbox) => "vp-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("voicecon-presence-v1:" + inbox)))[..36].ToLowerInvariant();

    public PresenceInfo Get(string friendId)
    {
        lock (gate)
        {
            if (!latest.TryGetValue(friendId, out var last)) return new(PresenceStatus.Unknown, null);
            return Evaluate(last.State, last.Time, DateTimeOffset.UtcNow);
        }
    }
    public static PresenceInfo Evaluate(string state, long time, DateTimeOffset now)
    {
        var seen = DateTimeOffset.FromUnixTimeSeconds(time);
        if (state == "off" || now - seen > Stale) return new(PresenceStatus.Offline, seen);
        return new(state == "call" ? PresenceStatus.InCall : PresenceStatus.Online, seen);
    }

    public void Start(IEnumerable<FriendCard> friends, bool showOnline)
    {
        visible = showOnline; SetFriends(friends);
        heartbeat ??= Task.Run(HeartbeatLoop);
    }

    /// <summary>Re-subscribes when the friend list (or the own card) changes.</summary>
    public void SetFriends(IEnumerable<FriendCard> friends, bool subscribe = true)
    {
        lock (gate)
        {
            watched.Clear();
            foreach (var friend in friends) watched[friend.Id] = (friend.Server.TrimEnd('/'), TopicFor(friend.Inbox));
            foreach (var id in latest.Keys.Where(id => !watched.ContainsKey(id)).ToArray()) latest.Remove(id);
        }
        subscription?.Cancel(); subscription?.Dispose(); subscription = null;
        if (!subscribe) return;
        var token = subscription = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        List<IGrouping<string, KeyValuePair<string, (string Server, string Topic)>>> servers;
        lock (gate) servers = watched.GroupBy(p => p.Value.Server).ToList();
        foreach (var server in servers)
        {
            var topics = server.Select(p => p.Value.Topic).Distinct().ToArray();
            _ = Task.Run(() => Listen(server.Key, topics, token.Token));
        }
    }

    public void UpdateOwn(FriendCard card) { own = card; _ = PublishAsync(force: true); }

    public void SetVisible(bool show)
    {
        if (visible == show) return; visible = show;
        _ = show ? PublishAsync(force: true) : PublishRawAsync("off");
    }
    public void SetInCall(bool inCall)
    {
        var next = inCall ? "call" : "on"; if (ownState == next) return; ownState = next; _ = PublishAsync(force: false);
    }
    /// <summary>Best-effort "offline" on exit or sleep. Waits at most <paramref name="timeout"/>.</summary>
    public void PublishOffline(TimeSpan timeout)
    {
        if (!visible) return;
        try { Task.Run(() => PublishRawAsync("off")).Wait(timeout); } catch (AggregateException) { }
    }
    public Task Resume() => PublishAsync(force: true);

    private async Task HeartbeatLoop()
    {
        await PublishAsync(force: true);
        while (!lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(Heartbeat, lifetime.Token); } catch (OperationCanceledException) { return; }
            await PublishAsync(force: true);
            // Re-evaluate staleness for everyone (a friend whose PC slept silently goes offline).
            string[] ids; lock (gate) ids = latest.Keys.ToArray(); foreach (var id in ids) Changed?.Invoke(id);
        }
    }

    private async Task PublishAsync(bool force)
    {
        if (!visible) return;
        var state = ownState;
        // At most one publication per 15 s, except the periodic heartbeat; repeated identical states are skipped.
        var now = Environment.TickCount64;
        if (!force && (state == lastPublished || now - Interlocked.Read(ref lastPublish) < 15_000)) { if (state != lastPublished) _ = DelayedPublish(); return; }
        await PublishRawAsync(state);
    }
    private int delayed;
    private async Task DelayedPublish()
    {
        if (Interlocked.Exchange(ref delayed, 1) == 1) return;
        try { await Task.Delay(15_000, lifetime.Token); } catch (OperationCanceledException) { return; } finally { Interlocked.Exchange(ref delayed, 0); }
        if (ownState != lastPublished) await PublishRawAsync(ownState);
    }
    private async Task PublishRawAsync(string state)
    {
        try
        {
            var body = JsonSerializer.Serialize(new Message(1, state, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            using var request = new HttpRequestMessage(HttpMethod.Post, own.Server.TrimEnd('/') + "/" + TopicFor(own.Inbox)) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
            request.Headers.Add("Firebase", "no");
            using var response = await http.SendAsync(request, lifetime.Token);
            if (response.IsSuccessStatusCode) { lastPublished = state; Interlocked.Exchange(ref lastPublish, Environment.TickCount64); }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TaskCanceledException or InvalidOperationException) { }
    }

    private async Task Listen(string server, string[] topics, CancellationToken token)
    {
        var retry = 1;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket(); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(50);
                var baseUri = new Uri(server);
                var since = (int)Stale.TotalMinutes + 5;
                var uri = new UriBuilder(baseUri) { Scheme = "wss", Path = baseUri.AbsolutePath.TrimEnd('/') + "/" + string.Join(",", topics) + "/ws", Query = $"since={since}m" }.Uri;
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token); connect.CancelAfter(TimeSpan.FromSeconds(20));
                await socket.ConnectAsync(uri, connect.Token); retry = 1;
                var buffer = new byte[8192];
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream(); WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (message.Length + result.Count > 8192) throw new IOException("Oversized presence event");
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    Handle(message.ToArray());
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or HttpRequestException or JsonException or InvalidOperationException or UriFormatException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, retry * 5)), token); } catch (OperationCanceledException) { return; }
            retry++;
        }
    }

    /// <summary>Processes one ntfy event (public for device-free checks).</summary>
    public bool Handle(byte[] raw)
    {
        JsonDocument json;
        try { json = JsonDocument.Parse(raw); } catch (JsonException) { return false; }
        using var document = json; var root = json.RootElement;
        if (!root.TryGetProperty("event", out var kind) || kind.GetString() != "message" || !root.TryGetProperty("topic", out var topicElement) || !root.TryGetProperty("message", out var body)) return false;
        var topic = topicElement.GetString(); if (topic is null) return false;
        Message? message;
        try { message = JsonSerializer.Deserialize<Message>(body.GetString() ?? ""); } catch (JsonException) { return false; }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (message is null || message.V != 1 || message.S is not ("on" or "call" or "off") || message.T < now - 86400 || message.T > now + 300) return false;
        string[] friends;
        lock (gate)
        {
            friends = watched.Where(p => p.Value.Topic == topic).Select(p => p.Key).ToArray();
            foreach (var id in friends.ToArray())
            {
                if (latest.TryGetValue(id, out var previous) && previous.Time > message.T) { friends = friends.Where(f => f != id).ToArray(); continue; }
                latest[id] = (message.S, message.T);
            }
        }
        foreach (var id in friends) Changed?.Invoke(id);
        return friends.Length > 0;
    }

    public void Dispose() { lifetime.Cancel(); subscription?.Cancel(); http.Dispose(); }
    private sealed record Message(int V, string S, long T);
}
