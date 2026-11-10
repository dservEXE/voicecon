using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Voicecon;

/// <summary>
/// One animation clock for the whole app, paced by the display: a background thread waits for each DWM composition
/// (DwmFlush = the monitor's refresh, 60/120/144 Hz) and posts a single frame tick to the UI thread.
/// It runs only while something animates and stops completely when idle. Respects the user's animation level.
/// </summary>
public static class UiMotion
{
    private sealed record Track(Control Owner, long Start, double Duration, Action<float> Step, Action? Done);
    private static readonly Dictionary<object, Track> tracks = [];
    private static readonly System.Windows.Forms.Timer fallback = new() { Interval = 10 };
    private static readonly AutoResetEvent wake = new(false);
    private static Thread? pump;
    private static volatile bool running;
    private static volatile Control? target;
    private static int posted;
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    public static MotionLevel Level { get; set; } = MotionLevel.Full;
    /// <summary>Compatibility switch: false is the same as <see cref="MotionLevel.Off"/>.</summary>
    public static bool Enabled { get => Level != MotionLevel.Off; set => Level = value ? (Level == MotionLevel.Off ? MotionLevel.Full : Level) : MotionLevel.Off; }
    public static bool IsIdle => !fallback.Enabled && !running;
    static UiMotion() { fallback.Tick += (_, _) => Tick(); }
    public static MotionLevel Parse(string? value) => value switch { "minimal" => MotionLevel.Minimal, "off" => MotionLevel.Off, _ => MotionLevel.Full };
    public static string Format(MotionLevel level) => level switch { MotionLevel.Minimal => "minimal", MotionLevel.Off => "off", _ => "full" };

    /// <param name="decorative">Purely ornamental motion (pulses, theme cross-fades): skipped in the minimal level.</param>
    public static void Run(object key, Control owner, int milliseconds, Action<float> step, Action? done = null, bool decorative = false)
    {
        Cancel(key);
        if (Level == MotionLevel.Off || decorative && Level == MotionLevel.Minimal || !CanAnimate(owner)) { step(1); done?.Invoke(); return; }
        var duration = Level == MotionLevel.Minimal ? milliseconds * 0.55 : milliseconds;
        tracks[key] = new Track(owner, Stopwatch.GetTimestamp(), duration / 1000d, step, done);
        Start(owner);
    }
    public static void Cancel(object key)
    {
        tracks.Remove(key); if (tracks.Count == 0) Stop();
    }
    public static void FinishAll()
    {
        var active = tracks.Values.ToArray(); tracks.Clear(); Stop();
        foreach (var item in active) if (!item.Owner.IsDisposed) { item.Step(1); item.Done?.Invoke(); }
    }

    private static void Start(Control owner)
    {
        var form = owner.FindForm();
        if (form is not { IsHandleCreated: true }) { fallback.Start(); return; }
        target = form;
        if (running) return;
        running = true;
        if (pump is null) { pump = new Thread(Pump) { IsBackground = true, Name = "Voicecon animation clock", Priority = ThreadPriority.AboveNormal }; pump.Start(); }
        wake.Set();
    }
    private static void Stop() { running = false; fallback.Stop(); }

    private static void Pump()
    {
        while (true)
        {
            if (!running) wake.WaitOne();
            if (!running) continue;
            // Wait for the next composed frame; fall back to ~120 Hz sleeps if DWM is unavailable.
            var ok = false; try { ok = DwmFlush() == 0; } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            if (!ok) Thread.Sleep(8);
            if (!running || Interlocked.Exchange(ref posted, 1) == 1) continue;
            var form = target;
            try
            {
                if (form is { IsDisposed: false, IsHandleCreated: true }) form.BeginInvoke(new Action(FrameFromPump));
                else { Interlocked.Exchange(ref posted, 0); running = false; }
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref posted, 0); running = false; }
        }
    }
    private static void FrameFromPump() { Interlocked.Exchange(ref posted, 0); Tick(); }

    private static void Tick()
    {
        foreach (var entry in tracks.ToArray())
        {
            var item = entry.Value;
            if (item.Owner.IsDisposed) { tracks.Remove(entry.Key); continue; }
            var hidden = !CanAnimate(item.Owner);
            var linear = hidden ? 1 : Math.Min(1, Stopwatch.GetElapsedTime(item.Start).TotalSeconds / item.Duration);
            // Ease-out (quartic): fast start, long soft landing.
            var eased = 1 - Math.Pow(1 - linear, 4); item.Step((float)eased);
            if (linear >= 1) { tracks.Remove(entry.Key); item.Done?.Invoke(); }
        }
        if (tracks.Count == 0) Stop();
    }
    private static bool CanAnimate(Control owner) => owner.Visible && owner.FindForm() is { Visible: true } form && (form.ContainsFocus || Form.ActiveForm == form || form.Owner is { } parent && (parent.ContainsFocus || Form.ActiveForm == parent));
}
