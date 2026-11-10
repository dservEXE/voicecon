using System.Reflection;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using Voicecon;
internal static class SessionFeatureChecks
{
    private static void Require(bool value, string description) { if (!value) throw new Exception(description); }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    public static void Check()
    {
        var gain = new InputGainProcessor(); var frame = Enumerable.Repeat((short)2000, 320).ToArray(); gain.Process(frame, 3);
        Require(frame[^1] == 6000 && frame[0] < 2100, "Microphone gain ramps smoothly to 300%");
        Array.Fill(frame, (short)2000); gain.Process(frame, 3); Require(frame.All(s => s == 6000), "Microphone gain increases quiet PCM");
        Array.Fill(frame, (short)32000); gain.Process(frame, 3); Require(frame.All(s => s > 0 && s < short.MaxValue), "Input boost limits without overflow");
        gain.Process(frame, 0); Array.Fill(frame, (short)1000); gain.Process(frame, 0); Require(frame.All(s => s == 0), "Microphone 0% is silence");
        Require(AudioLevels.Gain(500) == 3 && AudioLevels.Gain(-1) == 0, "Gain bounds 0–300%");
        var full = Measure(100, 100, true); var half = Measure(50, 100, true); var boost = Measure(300, 100, true); var master = Measure(100, 300, true); var mute = Measure(0, 100, false);
        Require(Math.Abs(half / full - 0.5) < 0.03 && Math.Abs(boost / full - 3) < 0.06 && Math.Abs(master / full - 3) < 0.06 && mute == 0, "Per-peer and master gain are independent and reach 300%");
        Console.WriteLine("PASS input gain 0–300%, smooth boost, bounded limiting, per-participant voice mute/attenuation/boost, independent master boost");
        using var form = new MainForm(true, "graphite"); _ = form.Handle; Prepare(form);
        // A stream window owns exactly one picture; frames replace each other and are disposed.
        using (var window = new StreamWindow("peer:1", "Друг · экран"))
        {
            Prepare(window); window.Size = new Size(800, 500); Prepare(window);
            var green = new Bitmap(320, 180); using (var graphics = Graphics.FromImage(green)) graphics.Clear(Color.Lime);
            window.ShowFrame(green); CheckPaint(window);
            var next = new Bitmap(320, 180); window.ShowFrame(next);
            var disposed = false; try { _ = green.Width; } catch (ArgumentException) { disposed = true; }
            Require(disposed, "Previous frame is released when the next arrives");
            var bounds = window.Bounds; window.ToggleFullscreen(); Require(window.FormBorderStyle == FormBorderStyle.None, "Fullscreen is borderless");
            var message = Message.Create(IntPtr.Zero, 0x100, IntPtr.Zero, IntPtr.Zero);
            typeof(StreamWindow).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [message, Keys.Escape]);
            Require(window.FormBorderStyle == FormBorderStyle.Sizable && window.Bounds == bounds, "Esc returns from fullscreen to the previous bounds");
        }
        Require(Get<SlimSlider>(form, "volume").Maximum == 300 && Get<SlimSlider>(form, "microphoneGain").Maximum == 300, "Settings sliders reach 300%");
        Invoke(form, "OpenSettings"); Require(Get<bool>(form, "settingsOpen") && Get<float>(form, "drawerProgress") == 1, "Settings drawer opens over the main screen");
        Invoke(form, "CloseSettings"); Require(!Get<bool>(form, "settingsOpen"), "Settings drawer closes");
        Invoke(form, "ShowPreviewCall"); var strip = Get<CallStrip>(form, "callStrip");
        Require(strip.Chips.OfType<CallStrip.PersonChip>().Count() == 2 && strip.Chips.OfType<CallStrip.OwnStreamChip>().Single().Viewers == 2 && strip.Chips.OfType<CallStrip.RemoteStreamChip>().Any(), "Call strip shows people, own stream with viewers and remote streams");
        Require(Get<SoftButton>(form, "endButton").Tone == ButtonTone.Danger && Get<SoftButton>(form, "shareButton").Tone == ButtonTone.Live, "Hang-up and stop-sharing are visually distinct");
        // Drag and click go through real window messages, so the framework's own capture and click logic is part of the test.
        static void Send(Control target, int message, int x, int y, bool down) { SendMessage(target.Handle, message, (IntPtr)(down ? 1 : 0), (IntPtr)((y << 16) | (x & 0xFFFF))); Application.DoEvents(); }
        static void Click(Control target, int x, int y) { Send(target, 0x201, x, y, true); Send(target, 0x202, x, y, false); }
        static void Drag(Control target, int from, int to, int y) { Send(target, 0x201, from, y, true); var step = Math.Sign(to - from) * 6; for (var x = from; step != 0 && (to - x) * Math.Sign(step) > 0; x += step) Send(target, 0x200, x, y, true); Send(target, 0x200, to, y, true); Send(target, 0x202, to, y, false); }
        using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(420, 200) })
        {
            var picker = new SegmentedPicker { Location = new Point(10, 10), Size = new Size(360, 52) };
            var toggle = new ToggleRow { Location = new Point(10, 80), Size = new Size(320, 52), Text = "Шумоподавление" };
            host.Controls.Add(picker); host.Controls.Add(toggle); _ = host.Handle; _ = picker.Handle; _ = toggle.Handle;
            picker.Options.AddRange([new("Обычный"), new("Игровой"), new("Эконом")]); picker.SetSilently(0); var changes = 0; picker.SelectedIndexChanged += (_, _) => changes++;
            Click(picker, 180, 26); Require(picker.SelectedIndex == 1 && changes == 1, "A click on a segment picks it");
            Drag(picker, 180, 330, 26); Require(picker.SelectedIndex == 2 && changes == 2, "Dragging the capsule to the end picks the last option once");
            Drag(picker, 300, 280, 26); Require(picker.SelectedIndex == 2 && changes == 2, "A short drag settles back without a change");
            Drag(picker, 300, 20, 26); Require(picker.SelectedIndex == 0 && changes == 3, "Dragging back to the start picks the first option");
            var x0 = toggle.Width - 44; var flips = 0; toggle.CheckedChanged += (_, _) => flips++;
            Click(toggle, x0, 20); Require(toggle.Checked && flips == 1, "A click on the switch turns it on");
            Click(toggle, x0, 20); Require(!toggle.Checked && flips == 2, "A second click turns it off");
            Click(toggle, 40, 20); Require(toggle.Checked && flips == 3, "A click on the label toggles too");
            Drag(toggle, x0 + 22, x0 - 14, 20); Require(!toggle.Checked && flips == 4, "Dragging the knob left switches off once");
            Drag(toggle, x0 - 14, x0 + 14, 20); Require(toggle.Checked && flips == 5, "Dragging the knob right switches on once");
            Drag(toggle, x0 + 14, x0 + 6, 20); Require(toggle.Checked && flips == 5, "A short drag slides back without a change");
            toggle.Focus(); SendMessage(toggle.Handle, 0x100, (IntPtr)0x20, IntPtr.Zero); SendMessage(toggle.Handle, 0x101, (IntPtr)0x20, IntPtr.Zero); Application.DoEvents();
            Require(!toggle.Checked && flips == 6, "Space still toggles");
        }
        // The page shown during the settings slide is rendered before it is on screen; it must be the real page, not an empty frame.
        var drawer = Get<Control>(form, "settingsDrawer"); typeof(MainForm).GetField("drawerProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, 0f);
        Invoke(form, "PlaceDrawer", true); drawer.Visible = true; drawer.PerformLayout(); Require(drawer.Left >= Get<Control>(form, "mainHost").Width, "The page waits off to the right before the slide");
        using (var page = (Bitmap)typeof(MainForm).GetMethod("RenderSettingsPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [])!)
        {
            int empty = 0, total = 0;
            for (var y = 0; y < page.Height; y += 7) for (var x = 0; x < page.Width; x += 7) { var c = page.GetPixel(x, y); total++; if (c.A < 255) empty++; }
            Require(page.Width > 100 && empty == 0, $"Settings page snapshot is fully painted ({empty} of {total} samples empty)");
        }
        drawer.Visible = false;
        Console.WriteLine("PASS stream window frames/fullscreen/Esc, 300% sliders, settings drawer, call strip, distinct hang-up vs stop-sharing, draggable segments and switches; no visible windows or capture");
    }
    private static void Prepare(Control c) { _ = c.Handle; c.PerformLayout(); foreach (Control child in c.Controls) Prepare(child); }
    private static void CheckPaint(Control target)
    {
        Prepare(target);
        using var canvas = new Bitmap(target.Width, target.Height); target.DrawToBitmap(canvas, target.ClientRectangle);
        var pixel = canvas.GetPixel(target.Width / 2, target.Height / 2); Require(pixel.G > 200 && pixel.R < 20, "Expanded video must actually paint its existing frame");
    }    private static double Measure(int peerVolume, int masterVolume, bool extraPeer)
    {
        using var mixer = new ConferenceMixer { Volume = AudioLevels.Gain(masterVolume) }; mixer.AddPeer("a"); mixer.SetPeerVolume("a", peerVolume);
        if (extraPeer) { mixer.AddPeer("b"); mixer.SetPeerVolume("b", 0); }
        using var encoder = OpusCodecFactory.CreateEncoder(16000, 1, OpusApplication.OPUS_APPLICATION_VOIP); encoder.Bitrate = 24000;
        var input = new short[320]; var encoded = new byte[1275];
        for (var n = 0; n < 8; n++)
        {
            for (var i = 0; i < 320; i++) input[i] = (short)(Math.Sin((n * 320 + i) * Math.PI * 2 * 400 / 16000) * 2000);
            var length = encoder.Encode(input, 320, encoded, encoded.Length); var packet = encoded[..length]; mixer.Receive("a", (ushort)n, packet); if (extraPeer) mixer.Receive("b", (ushort)n, packet);
        }
        var pcm = new byte[640]; double sum = 0; for (var i = 0; i < 6; i++) { mixer.Read(pcm, 0, pcm.Length); foreach (var sample in MemoryMarshal.Cast<byte, short>(pcm)) sum += (double)sample * sample; }
        return Math.Sqrt(sum / (320 * 6));
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
