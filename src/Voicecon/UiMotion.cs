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
    private sealed record Track(Control Owner, long Start, double Duration, Action<float> Step, Action? Done, bool Glide);
    private static readonly Dictionary<object, Track> tracks = [];
    private static readonly System.Windows.Forms.Timer fallback = new() { Interval = 10 };
    private static readonly AutoResetEvent wake = new(false);
    private static Thread? pump;
    private static volatile bool running;
    private static volatile Control? target;
    private static int posted;
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    private const uint UpdateNow = 0x0100, AllChildren = 0x0080;

    public static MotionLevel Level { get; set; } = MotionLevel.Full;
    /// <summary>For timing checks only: treat visible windows as active even when another app has the focus.</summary>
    public static bool AssumeActive { get; set; }
    /// <summary>Compatibility switch: false is the same as <see cref="MotionLevel.Off"/>.</summary>
    public static bool Enabled { get => Level != MotionLevel.Off; set => Level = value ? (Level == MotionLevel.Off ? MotionLevel.Full : Level) : MotionLevel.Off; }
    public static bool IsIdle => !fallback.Enabled && !running;
    static UiMotion() { fallback.Tick += (_, _) => Tick(); }
    public static MotionLevel Parse(string? value) => value switch { "minimal" => MotionLevel.Minimal, "off" => MotionLevel.Off, _ => MotionLevel.Full };
    public static string Format(MotionLevel level) => level switch { MotionLevel.Minimal => "minimal", MotionLevel.Off => "off", _ => "full" };

    /// <param name="decorative">Purely ornamental motion (pulses, theme cross-fades): skipped in the minimal level.</param>
    /// <param name="glide">Ease in and out (for panels that travel far); otherwise a cubic ease-out.</param>
    public static void Run(object key, Control owner, int milliseconds, Action<float> step, Action? done = null, bool decorative = false, bool glide = false)
    {
        Cancel(key);
        if (Level == MotionLevel.Off || decorative && Level == MotionLevel.Minimal || !CanAnimate(owner)) { step(1); done?.Invoke(); return; }
        var duration = Level == MotionLevel.Minimal ? milliseconds * 0.55 : milliseconds;
        tracks[key] = new Track(owner, Stopwatch.GetTimestamp(), duration / 1000d, step, done, glide);
        Start(owner);
    }
    /// <summary>True when <see cref="Run"/> would really animate for this control (not off, window visible and active).</summary>
    public static bool WillAnimate(Control owner) => Level != MotionLevel.Off && CanAnimate(owner);
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
            // One frame in flight at a time: the next tick is posted only after the previous one has painted.
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
    private static void FrameFromPump() { try { Tick(); } finally { Interlocked.Exchange(ref posted, 0); } }

    private static void Tick()
    {
        var forms = new HashSet<Form>();
        foreach (var entry in tracks.ToArray())
        {
            var item = entry.Value;
            if (item.Owner.IsDisposed) { tracks.Remove(entry.Key); continue; }
            var hidden = !CanAnimate(item.Owner);
            var linear = hidden ? 1 : Math.Min(1, Stopwatch.GetElapsedTime(item.Start).TotalSeconds / item.Duration);
            item.Step((float)Ease(linear, item.Glide));
            if (item.Owner.FindForm() is { IsHandleCreated: true } form) forms.Add(form);
            if (linear >= 1) { tracks.Remove(entry.Key); item.Done?.Invoke(); }
        }
        // Paint everything this frame invalidated right now, so a moving panel never leaves stale pixels behind
        // (otherwise queued ticks can starve WM_PAINT and the uncovered area shows trails).
        foreach (var form in forms) if (!form.IsDisposed && form.Visible) RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero, UpdateNow | AllChildren);
        if (tracks.Count == 0) Stop();
    }
    /// <summary>Cubic ease-out by default; the glide is a smooth ease-in-out for long travel.</summary>
    public static double Ease(double t, bool glide) => glide ? (t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2) : 1 - Math.Pow(1 - t, 3);
    private static bool CanAnimate(Control owner) => owner.Visible && owner.FindForm() is { Visible: true } form && (AssumeActive || form.ContainsFocus || Form.ActiveForm == form || form.Owner is { } parent && (parent.ContainsFocus || Form.ActiveForm == parent));
}

/// <summary>
/// Two snapshots sliding over each other (a page over the view under it). While a panel travels, only this one
/// control repaints — two GDI blits a frame — so the slide holds the monitor's refresh rate however busy the page is.
/// </summary>
public sealed class SlideOverlay : Control
{
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    private const int Copy = 0x00CC0020;
    private IntPtr under, over;
    private int edge;
    public bool Active => under != IntPtr.Zero;
    public SlideOverlay()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, false);
        TabStop = false; Visible = false;
    }
    /// <summary>What a visible control shows right now, read back from the window surface (children included).</summary>
    public static Bitmap FromScreen(Control control)
    {
        var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
        using var g = Graphics.FromImage(bitmap); var target = g.GetHdc(); var source = GetDC(control.Handle);
        try { BitBlt(target, 0, 0, bitmap.Width, bitmap.Height, source, 0, 0, Copy); }
        finally { ReleaseDC(control.Handle, source); g.ReleaseHdc(target); }
        return bitmap;
    }
    /// <summary>A control rendered off-screen (it may be hidden behind others or outside its parent).</summary>
    public static Bitmap Render(Control control)
    {
        var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); return bitmap;
    }
    public void Begin(Bitmap below, Bitmap above, int start)
    {
        End(); under = below.GetHbitmap(); over = above.GetHbitmap(); below.Dispose(); above.Dispose();
        edge = start; Visible = true; BringToFront(); Invalidate();
    }
    /// <summary>Moves the page's left edge; only the strip that changed is repainted.</summary>
    public void SetEdge(int x)
    {
        if (x == edge) return;
        var shade = Shade;
        var left = Math.Min(x, edge) - shade; var right = Math.Max(x, edge) + 1;
        edge = x; Invalidate(new Rectangle(left, 0, right - left, Height));
    }
    private int Shade => (int)(22 * DeviceDpi / 96f);
    public void End()
    {
        Visible = false;
        if (under != IntPtr.Zero) DeleteObject(under); if (over != IntPtr.Zero) DeleteObject(over);
        under = over = IntPtr.Zero;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (!Active) { e.Graphics.Clear(UiTheme.Current.Background); return; }
        var clip = e.ClipRectangle; var x = Math.Clamp(edge, 0, Width);
        var hdc = e.Graphics.GetHdc(); var memory = CreateCompatibleDC(hdc);
        try
        {
            if (clip.Left < x) { var old = SelectObject(memory, under); BitBlt(hdc, clip.Left, clip.Top, Math.Min(x, clip.Right) - clip.Left, clip.Height, memory, clip.Left, clip.Top, Copy); SelectObject(memory, old); }
            if (clip.Right > x) { var from = Math.Max(x, clip.Left); var old = SelectObject(memory, over); BitBlt(hdc, from, clip.Top, clip.Right - from, clip.Height, memory, from - x, clip.Top, Copy); SelectObject(memory, old); }
        }
        finally { DeleteDC(memory); e.Graphics.ReleaseHdc(hdc); }
        // A soft shadow along the travelling edge, so the page reads as sliding over the view.
        var shade = Shade;
        if (x > 0 && x < Width && clip.Left < x)
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(x - shade - 1, 0, shade + 1, Math.Max(1, Height)), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(90, 0, 0, 0), 0f);
            e.Graphics.FillRectangle(brush, x - shade, 0, shade, Height);
        }
    }
    protected override void Dispose(bool disposing) { End(); base.Dispose(disposing); }
}
