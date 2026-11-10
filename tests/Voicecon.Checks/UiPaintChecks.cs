using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Drawing.Imaging;
using Voicecon;

internal static class UiPaintChecks
{
    public static void CheckRepaints()
    {
        var paintMethod = typeof(Control).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var count = 0;
        foreach (var theme in UiTheme.All)
        {
            UiTheme.Select(theme.Id);
            using var parent = new Panel { BackColor = theme.Surface };
            using var toggle = new ToggleRow { Text = "Шумоподавление", Description = "Убирает ровный фоновый шум", Checked = true };
            using var slider = new SlimSlider { Value = 70 };
            using var button = new SoftButton { Text = "Микрофон", Glyph = UiIcon.Microphone };
            using var combo = new ThemedComboBox(); combo.Items.AddRange(["Первое устройство", "Другое устройство"]); combo.SelectedIndex = 0;
            using var mark = new BrandMark();
            using var card = new SurfacePanel();
            foreach (var control in new Control[] { toggle, slider, button, combo, mark, card })
            {
                parent.Controls.Add(control); UiPainting.ApplyBackgrounds(parent);
                foreach (var width in new[] { 280, 340, 315 })
                {
                    control.Size = new Size(width, 52);
                    if (control == toggle) toggle.Checked = !toggle.Checked;
                    if (control == slider) slider.Value = width % 150;
                    if (control == combo) combo.SelectedIndex = 1 - combo.SelectedIndex;
                    var a = Paint(control, Color.Magenta, null); var b = Paint(control, Color.Lime, null);
                    if (a != b) throw new Exception($"Paint depends on stale buffer contents: {control.GetType().Name}, {theme.Id}");
                    var clip = new Rectangle(20, 8, width - 40, 36);
                    using var bitmap = new Bitmap(width, 52); using var graphics = Graphics.FromImage(bitmap);
                    graphics.Clear(Color.Magenta); graphics.SetClip(clip);
                    paintMethod.Invoke(control, [new PaintEventArgs(graphics, clip)]);
                    if (bitmap.GetPixel(0, 0).ToArgb() != Color.Magenta.ToArgb() || bitmap.GetPixel(width - 1, 25).ToArgb() != Color.Magenta.ToArgb())
                        throw new Exception($"Paint escaped its invalid region: {control.GetType().Name}, {theme.Id}");
                    count++;
                }
                parent.Controls.Remove(control);
            }
        }
        using var form = new MainForm(true, "graphite"); Prepare(form); CheckBackgrounds(form);
        using var network = new NetworkDialog(new Settings()); Prepare(network); CheckBackgrounds(network);
        var on = UiSounds.CreatePcm(true); var off = UiSounds.CreatePcm(false);
        if (on.Length != 4960 || off.Length != on.Length || on.SequenceEqual(off)) throw new Exception("Microphone cues must have equal duration and distinct waveforms");
        if (on[0] != 0 || on[1] != 0 || on[^1] != 0 || on[^2] != 0) throw new Exception("Cue edges must fade to zero");
        Console.WriteLine($"PASS {count} repaint/resize cases across {UiTheme.All.Length} themes: reused buffers cleared, partial paint clipped; main/network backgrounds opaque; two distinct 155 ms local cues generated without playback.");

        string Paint(Control control, Color seed, Rectangle? clip)
        {
            using var bitmap = new Bitmap(control.Width, control.Height); using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(seed); if (clip is { } region) graphics.SetClip(region);
            paintMethod.Invoke(control, [new PaintEventArgs(graphics, clip ?? control.ClientRectangle)]);
            using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Bmp);
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }
        static void CheckBackgrounds(Control control)
        {
            if (control.BackColor.A != 255) throw new Exception("Transparent background remains: " + control.GetType().Name);
            foreach (Control child in control.Controls) CheckBackgrounds(child);
        }
    }
    public static void Benchmark()
    {
        using var form = new MainForm(true, "graphite"); Prepare(form);
        using var canvas = new Bitmap(1500, 1100);
        var layout = new List<double>(); var paint = new List<double>();
        for (var i = 0; i < 15; i++)
        {
            var clock = Stopwatch.StartNew();
            form.ClientSize = new Size(1000 + (i % 5) * 50, 770 + (i % 4) * 20); form.PerformLayout();
            var layoutMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart(); form.DrawToBitmap(canvas, new Rectangle(0, 0, form.Width, form.Height));
            if (i >= 3) { layout.Add(layoutMs); paint.Add(clock.Elapsed.TotalMilliseconds); }
        }
        Console.WriteLine($"OFFSCREEN resize: layout avg {layout.Average():F2} ms, max {layout.Max():F2} ms; full bitmap paint avg {paint.Average():F2} ms, max {paint.Max():F2} ms; 12 measured frames, no visible window.");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    /// <summary>Opt-in (shows a real window briefly): real settings slides, timed frame by frame on the vsync clock.</summary>
    public static void MotionBenchmark()
    {
        using var form = new MainForm(true, "graphite") { StartPosition = FormStartPosition.Manual, Location = new Point(60, 60) };
        UiMotion.AssumeActive = true; UiMotion.Level = MotionLevel.Full; form.Show(); form.Activate(); form.Refresh(); Application.DoEvents();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var progress = typeof(MainForm).GetField("drawerProgress", flags)!;
        if (!UiMotion.WillAnimate(form)) { Console.WriteLine($"MOTION skipped: visible {form.Visible}, level {UiMotion.Level}"); return; }
        var host = (Control)typeof(MainForm).GetField("mainHost", flags)!.GetValue(form)!;
        using (var shot = SlideOverlay.FromScreen(host)) shot.Save(Path.GetFullPath("artifacts/previews/slide-snapshot.png"));
        typeof(MainForm).GetMethod("WarmSettings", flags)!.Invoke(form, []);
        var intervals = new List<double>(); var starts = new List<double>();
        foreach (var name in new[] { "OpenSettings", "CloseSettings", "OpenSettings", "CloseSettings" })
        {
            var clock = Stopwatch.StartNew(); typeof(MainForm).GetMethod(name, flags)!.Invoke(form, []); starts.Add(clock.Elapsed.TotalMilliseconds);
            var last = (float)progress.GetValue(form)!; var lastTime = clock.Elapsed.TotalMilliseconds; var first = true;
            while (!UiMotion.IsIdle && clock.ElapsedMilliseconds < 2000)
            {
                Application.DoEvents();
                var now = (float)progress.GetValue(form)!;
                if (now != last) { var t = clock.Elapsed.TotalMilliseconds; if (!first) intervals.Add(t - lastTime); first = false; lastTime = t; last = now; }
                else Thread.Sleep(0);
            }
            Application.DoEvents(); form.Refresh();
        }
        form.Hide(); intervals.Sort();
        Console.WriteLine($"MOTION settings slide: {intervals.Count} frames over 4 slides, median interval {intervals[intervals.Count / 2]:F2} ms, p95 {intervals[(int)(intervals.Count * 0.95)]:F2} ms, max {intervals[^1]:F2} ms; snapshot before slide {string.Join(" / ", starts.Select(v => v.ToString("F0")))} ms");
    }
    private static void Prepare(Control control)
    {
        _ = control.Handle; control.PerformLayout();
        foreach (Control child in control.Controls) Prepare(child);
    }
}
