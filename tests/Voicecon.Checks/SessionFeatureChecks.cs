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
        // Drag: the segment capsule and the switch knob follow the pointer and settle where they are let go.
        void Mouse(Control target, string name, int x, int y) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, [typeof(MouseEventArgs)])!.Invoke(target, [new MouseEventArgs(MouseButtons.Left, 1, x, y, 0)]);
        using (var picker = new SegmentedPicker { Size = new Size(360, 52) })
        {
            picker.Options.AddRange([new("Обычный"), new("Игровой"), new("Эконом")]); picker.SetSilently(0); _ = picker.Handle; var changes = 0; picker.SelectedIndexChanged += (_, _) => changes++;
            Mouse(picker, "OnMouseDown", 40, 26); for (var x = 40; x <= 300; x += 20) Mouse(picker, "OnMouseMove", x, 26); Mouse(picker, "OnMouseUp", 300, 26);
            Require(picker.SelectedIndex == 2 && changes == 1, "Dragging the capsule to the end picks the last option once");
            Mouse(picker, "OnMouseDown", 300, 26); Mouse(picker, "OnMouseMove", 270, 26); Mouse(picker, "OnMouseUp", 270, 26);
            Require(picker.SelectedIndex == 2 && changes == 1, "A short drag settles back without a change");
        }
        using (var toggle = new ToggleRow { Size = new Size(320, 52), Text = "Шумоподавление" })
        {
            _ = toggle.Handle; var x0 = toggle.Width - 44;
            Mouse(toggle, "OnMouseDown", x0, 20); Mouse(toggle, "OnMouseMove", x0 + 10, 20); Mouse(toggle, "OnMouseMove", x0 + 30, 20); Mouse(toggle, "OnMouseUp", x0 + 30, 20);
            toggle.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toggle, [EventArgs.Empty]);
            Require(toggle.Checked, "Dragging the knob across switches on, and the click after the drag does not undo it");
            toggle.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toggle, [EventArgs.Empty]);
            Require(!toggle.Checked, "A plain click still toggles");
        }
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
}
