using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Voicecon;

/// <summary>One idle websocket for encrypted call invitations. Never transports media or chat.</summary>
public sealed class CallMailbox : IDisposable
{
    private readonly ContactBook book;
    private readonly FriendCard own;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HashSet<string> seen = [];
    private Task? listener;
    public bool Connected { get; private set; }
    public event Action<bool>? ConnectivityChanged;
    public event Action<FriendCard, CallSignal>? Received;
    public CallMailbox(ContactBook book, FriendCard own) { this.book = book; this.own = own; }
    public void Start() => listener ??= Task.Run(Listen);
    public async Task SendAsync(FriendCard friend, CallSignal signal)
    {
        var body = book.Seal(friend, signal);
        if (Encoding.UTF8.GetByteCount(body) > 4000) throw new InvalidOperationException("Слишком большой сетевой код для доставки. Используйте подключение по коду или свой сервис ntfy с увеличенным лимитом.");
        using var request = new HttpRequestMessage(HttpMethod.Post, friend.Server + "/" + friend.Inbox) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        request.Headers.Add("Cache", "no"); request.Headers.Add("Firebase", "no");
        using var response = await http.SendAsync(request, lifetime.Token);
        if ((int)response.StatusCode == 429) throw new InvalidOperationException("Сервис доставки временно ограничил запросы. Подождите или используйте подключение по коду.");
        response.EnsureSuccessStatusCode();
    }
    private async Task Listen()
    {
        var retry = 1;
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket(); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(45);
                var uri = new UriBuilder(own.Server) { Scheme = "wss", Path = new Uri(own.Server).AbsolutePath.TrimEnd('/') + "/" + own.Inbox + "/ws" }.Uri;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await socket.ConnectAsync(uri, timeout.Token); Connected = true; ConnectivityChanged?.Invoke(true); retry = 1;
                var bytes = new byte[8192];
                while (!lifetime.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream(); WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), lifetime.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (message.Length + result.Count > 16_384) throw new IOException("Oversized signaling event");
                        message.Write(bytes, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    using var json = JsonDocument.Parse(message.ToArray());
                    if (!json.RootElement.TryGetProperty("event", out var kind) || kind.GetString() != "message" || !json.RootElement.TryGetProperty("message", out var body)) continue;
                    var opened = book.Open(body.GetString() ?? ""); if (opened is not { } item) continue;
                    var stamp = item.Friend.Id + item.Signal.Kind + item.Signal.Id;
                    if (!seen.Add(stamp)) continue; if (seen.Count > 512) { seen.Clear(); seen.Add(stamp); }
                    Received?.Invoke(item.Friend, item.Signal);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or HttpRequestException or JsonException or InvalidOperationException) { }
            Connected = false; ConnectivityChanged?.Invoke(false);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, retry * 3)), lifetime.Token); } catch (OperationCanceledException) { break; }
            retry++;
        }
    }
    public void Dispose() { lifetime.Cancel(); http.Dispose(); }
}
