using System.Diagnostics;

namespace Voicecon;

/// <summary>A single timer, stopped whenever there is nothing to animate.</summary>
public static class UiMotion
{
    private sealed record Track(Control Owner, long Start, double Duration, Action<float> Step, Action? Done);
    private static readonly Dictionary<object, Track> tracks = [];
    private static readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    public static bool Enabled { get; set; } = true;
    public static bool IsIdle => !timer.Enabled;
    static UiMotion() { timer.Tick += (_, _) => Tick(); }
    public static void Run(object key, Control owner, int milliseconds, Action<float> step, Action? done = null)
    {
        Cancel(key);
        if (!CanAnimate(owner)) { step(1); done?.Invoke(); return; }
        tracks[key] = new Track(owner, Stopwatch.GetTimestamp(), milliseconds / 1000d, step, done); timer.Start();
    }
    public static void Cancel(object key)
    {
        tracks.Remove(key); if (tracks.Count == 0) timer.Stop();
    }
    public static void FinishAll()
    {
        var active = tracks.Values.ToArray(); tracks.Clear(); timer.Stop();
        foreach (var item in active) if (!item.Owner.IsDisposed) { item.Step(1); item.Done?.Invoke(); }
    }
    private static void Tick()
    {
        foreach (var entry in tracks.ToArray())
        {
            var item = entry.Value;
            if (item.Owner.IsDisposed) { tracks.Remove(entry.Key); continue; }
            var hidden = !CanAnimate(item.Owner);
            var linear = hidden ? 1 : Math.Min(1, Stopwatch.GetElapsedTime(item.Start).TotalSeconds / item.Duration);
            var eased = 1 - Math.Pow(1 - linear, 3); item.Step((float)eased);
            if (linear >= 1) { tracks.Remove(entry.Key); item.Done?.Invoke(); }
        }
        if (tracks.Count == 0) timer.Stop();
    }
    private static bool CanAnimate(Control owner) => Enabled && owner.Visible && owner.FindForm() is { Visible: true } form && (form.ContainsFocus || Form.ActiveForm == form);
}
