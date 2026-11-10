using System.Diagnostics;
using System.Text.Json;
namespace Voicecon;
public sealed class LatencyProbe
{
    private long pending, started; private double last = -1; private long received;
    public double? Milliseconds => Stopwatch.GetElapsedTime(Interlocked.Read(ref received)).TotalSeconds < 5 && Volatile.Read(ref last) >= 0 ? Volatile.Read(ref last) : null;
    public string? Query()
    {
        var now = Stopwatch.GetTimestamp();
        if (Interlocked.Read(ref pending) != 0 && Stopwatch.GetElapsedTime(started, now).TotalSeconds < 4) return null;
        Interlocked.Exchange(ref started, now); Interlocked.Exchange(ref pending, now);
        return $"{{\"_vcprobe\":\"q\",\"id\":{now}}}";
    }
    public bool Handle(string text, Action<string> send)
    {
        if (!text.Contains("\"_vcprobe\"", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text); var root = doc.RootElement;
            if (!root.TryGetProperty("_vcprobe", out var type) || !root.TryGetProperty("id", out var id) || !id.TryGetInt64(out var nonce)) return true;
            if (type.GetString() == "q") send($"{{\"_vcprobe\":\"a\",\"id\":{nonce}}}");
            else if (type.GetString() == "a" && nonce != 0 && Interlocked.CompareExchange(ref pending, 0, nonce) == nonce)
            { Volatile.Write(ref last, Stopwatch.GetElapsedTime(nonce).TotalMilliseconds); Interlocked.Exchange(ref received, Stopwatch.GetTimestamp()); }
        }
        catch (JsonException) { }
        return true;
    }
}
public sealed record LatencyPoint(DateTimeOffset Time, string Peer, double? Milliseconds);
public sealed class LatencyHistory
{
    private readonly List<LatencyPoint> points = [];
    public IReadOnlyList<LatencyPoint> Points => points;
    public void Add(DateTimeOffset now, IEnumerable<(string Name, double? Ping)> peers)
    { points.RemoveAll(p => p.Time < now.AddSeconds(-120)); foreach (var peer in peers.Take(5)) points.Add(new(now, peer.Name, peer.Ping)); }
    public void Clear() => points.Clear();
}
public sealed class LatencyGraph : Control
{
    public LatencyHistory History { get; }
    public LatencyGraph(LatencyHistory history) { History = history; DoubleBuffered = true; Dock = DockStyle.Fill; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var t = UiTheme.Current; Backdrop.Paint(e.Graphics, this, ClientRectangle); var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var plot = new Rectangle(55, 34, Math.Max(10, Width - 78), Math.Max(10, Height - 95)); var now = DateTimeOffset.UtcNow;
        var max = Math.Max(50, Math.Ceiling(History.Points.Select(p => p.Milliseconds ?? 0).DefaultIfEmpty(0).Max() / 50) * 50);
        using var grid = new Pen(t.Border); using var text = new SolidBrush(t.Muted);
        for (var i = 0; i <= 4; i++) { var y = plot.Bottom - i * plot.Height / 4; g.DrawLine(grid, plot.Left, y, plot.Right, y); g.DrawString($"{max * i / 4:0}", UiTheme.Small, text, 3, y - 7); }
        g.DrawString("мс · RTT", UiTheme.Small, text, 6, 8); g.DrawString("−120 с", UiTheme.Small, text, plot.Left, plot.Bottom + 6); g.DrawString("сейчас", UiTheme.Small, text, plot.Right - 42, plot.Bottom + 6);
        var colors = new[] { t.Accent, t.Online, t.Glow, Color.Salmon, Color.CornflowerBlue }; var n = 0;
        foreach (var series in History.Points.GroupBy(p => p.Peer))
        {
            var color = colors[n++ % colors.Length]; using var pen = new Pen(color, 2); PointF? previous = null; DateTimeOffset prevTime = default;
            foreach (var p in series)
            {
                if (p.Milliseconds is not { } value || (now - p.Time).TotalSeconds > 120) { previous = null; continue; }
                var point = new PointF(plot.Right - (float)((now - p.Time).TotalSeconds / 120 * plot.Width), plot.Bottom - (float)(value / max * plot.Height));
                if (previous is { } before && (p.Time - prevTime).TotalSeconds < 3) g.DrawLine(pen, before, point); previous = point; prevTime = p.Time;
            }
            using var brush = new SolidBrush(color); g.DrawString(series.Key, UiTheme.Small, brush, plot.Left + (n - 1) * 105, Height - 28);
        }
        if (History.Points.Count == 0) g.DrawString("История появится во время звонка", UiTheme.Body, text, plot.Left, plot.Top + 30);
    }
}
public sealed partial class MainForm
{
    private readonly SoftButton pingButton = new() { Text = "Пинг —", Tone = ButtonTone.Quiet };
    private readonly LatencyHistory latency = new();
    private LatencyGraph? latencyGraph;
    private void SampleLatency()
    {
        var peers = room?.SampleLatency() ?? [];
        latency.Add(DateTimeOffset.UtcNow, peers);
        var values = peers.Where(p => p.Ping.HasValue).Select(p => p.Ping!.Value).ToArray();
        var routes = room?.ConnectionRoutes.Select(p => p.Route).Distinct().ToArray() ?? [];
        var route = routes.Length switch { 1 => routes[0], > 1 => "разные пути", _ => "маршрут…" };
        pingButton.Text = room is not { IsDisposed: false, ConnectedCount: > 0 } ? "Пинг" : values.Length > 0 ? $"{values.Max():0} мс · {route}" : $"— мс · {route}";
        if (latencyGraph is { IsDisposed: false }) latencyGraph.Invalidate();
    }
    private void ShowLatency()
    {
        using var dialog = new ThemedDialog { Text = "Задержка соединения · последние 2 минуты", ClientSize = new Size(660, 390), MinimumSize = new Size(440, 290), StartPosition = FormStartPosition.CenterParent, Icon = Icon, Font = UiTheme.Body };
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(14) }; dialog.Padding = new Padding(8); dialog.Controls.Add(card);
        var content = Grid(1); card.Controls.Add(content);
        var routes = room?.ConnectionRoutes ?? [];
        var routeText = routes.Count == 0 ? "Маршрут появится после подключения." : string.Join("   ·   ", routes.Select(p => $"{p.Name}: {p.Route}"));
        var routeLabel = TextLabel(routeText, 9, TextTone.Muted); routeLabel.AutoEllipsis = true; AddRow(content, routeLabel, 28);
        latencyGraph = new LatencyGraph(latency); AddRow(content, latencyGraph, 0); content.RowStyles[^1].SizeType = SizeType.Percent; content.RowStyles[^1].Height = 100;
        UiPainting.ApplyBackgrounds(dialog); dialog.ShowDialog(this); latencyGraph = null;
    }
}
