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
        using var form = new MainForm(true, "graphite"); _ = form.Handle;
        var page = Get<Control>(form, "videoPage"); Invoke(form, "ShowContent", page); Prepare(form);
        var green = new Bitmap(320, 180); using (var graphics = Graphics.FromImage(green)) graphics.Clear(Color.Lime);
        Invoke(form, "DisplayVideo", "peer:1", "Друг", (byte)1, green);
        Invoke(form, "DisplayVideo", "peer:2", "Друг", (byte)2, new Bitmap(320, 240));
        var pictures = Get<Dictionary<string, PictureBox>>(form, "pictures"); var first = pictures["peer:1"]; var bitmap = first.Image!; var home = first.Parent;
        Invoke(form, "SetVideoMode", "peer:1", VideoViewMode.Wide);
        CheckPaint(Get<Panel>(form, "wideVideoHost"));
        Require(first.Parent == Get<Panel>(form, "widePictureHost") && ReferenceEquals(first.Image, bitmap), "Wide mode reparents existing image");
        Invoke(form, "SetVideoMode", "peer:1", VideoViewMode.Fullscreen);
        var viewer = Get<VideoViewer>(form, "viewer"); Require(viewer.Contains(first) && !viewer.Visible, "Fullscreen owns existing surface without showing a test window");
        viewer.Size = new Size(1280, 720); CheckPaint(viewer);
        var message = Message.Create(IntPtr.Zero, 0x100, IntPtr.Zero, IntPtr.Zero);
        typeof(VideoViewer).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, [message, Keys.Escape]);
        Require(Get<VideoViewMode>(form, "videoMode") == VideoViewMode.Wide && ReferenceEquals(first.Image, bitmap), "Esc restores previous wide mode without disposing image");
        Invoke(form, "SetVideoMode", "peer:1", VideoViewMode.Normal);
        Require(first.Parent == home && ((TableLayoutPanel)home!).GetRow(first) == 1 && bitmap.Width == 320, "Normal mode restores correct grid cell");
        for (var i = 0; i < 20; i++) { Invoke(form, "SetVideoMode", "peer:1", VideoViewMode.Fullscreen); Application.DoEvents(); Invoke(form, "SetVideoMode", "peer:2", VideoViewMode.Wide); Application.DoEvents(); Invoke(form, "SetVideoMode", "peer:2", VideoViewMode.Normal); }
        Invoke(form, "SetVideoMode", "peer:2", VideoViewMode.Fullscreen); Invoke(form, "StopRemoteVideo", "peer", (byte)2, (uint)10);
        Require(pictures.Count == 1 && first.Image!.Width == 320 && Get<VideoViewMode>(form, "videoMode") == VideoViewMode.Normal, "Remote stop closes expanded camera only");
        Invoke(form, "SetVideoMode", "peer:1", VideoViewMode.Fullscreen); Invoke(form, "StopAllVideo");
        Require(pictures.Count == 0 && Get<Dictionary<string, Control>>(form, "pictureHomes").Count == 0, "Hangup clears viewer and surfaces");
        Require(Get<SlimSlider>(form, "volume").Maximum == 300 && Get<SlimSlider>(form, "participantVoice").Maximum == 300 && Get<SlimSlider>(form, "microphoneGain").Maximum == 300, "Settings sliders reach 300%");
        Console.WriteLine("PASS wide/fullscreen/normal for screen + camera, Esc return, 20 repeated mode changes, image ownership, remote stop/hangup, 300% settings; no visible windows or capture");
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
