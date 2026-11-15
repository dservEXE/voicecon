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
        CheckEconomyVoice();
        CheckAvatars();
        CheckChatSelection();
        CheckLongWords();
        CheckAvatarPopup();
        CheckStage();
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
    /// <summary>
    /// Economy mode: the old 8 kbit/s, 20 ms profile against the new 12 kbit/s, 60 ms one on a speech-like signal.
    /// The new one must keep the upper speech band (clarity) and still send less over the wire, headers included.
    /// </summary>
    private static void CheckEconomyVoice()
    {
        Require(AudioEngine.Profile(VoiceMode.Economy) == (12000, 960, 10) && AudioEngine.Profile(VoiceMode.Gaming).FrameSamples == 320 && AudioEngine.Profile(VoiceMode.Normal).Bitrate == 24000, "Mode profiles");
        const int rate = 16000, seconds = 4; var source = new short[rate * seconds]; var random = new Random(7);
        for (var i = 0; i < source.Length; i++)
        {
            // Voiced speech stand-in: a gliding pitch with harmonics up to 7 kHz, syllable-rate loudness and a little breath noise.
            var t = (double)i / rate; var pitch = 140 + 40 * Math.Sin(2 * Math.PI * 0.7 * t); var envelope = 0.35 + 0.65 * Math.Max(0, Math.Sin(2 * Math.PI * 3.1 * t));
            double value = 0; for (var h = 1; pitch * h < 7200; h++) value += Math.Sin(2 * Math.PI * pitch * h * t + h) / Math.Sqrt(h);
            source[i] = (short)Math.Clamp((value * 1500 + (random.NextDouble() - 0.5) * 300) * envelope, short.MinValue, short.MaxValue);
        }
        (double High, double WireKbps, int Packets) Run(int bitrate, int frame, int complexity)
        {
            using var encoder = OpusCodecFactory.CreateEncoder(rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = bitrate; encoder.Complexity = complexity; encoder.UseInbandFEC = true; encoder.PacketLossPercent = 5; encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            using var jitter = new VoiceJitterBuffer(); var output = new byte[source.Length * 2]; var bytes = 0; var packets = 0; var buffer = new byte[1275];
            for (var start = 0; start + frame <= source.Length; start += frame)
            {
                var size = encoder.Encode(source.AsSpan(start, frame), frame, buffer.AsSpan(), buffer.Length); bytes += size; packets++;
                if (frame == 960) Require(VoiceJitterBuffer.Duration48k(buffer.AsSpan(0, size)) == 2880, "A 60 ms packet advances RTP time by 2880");
                // Packets arrive in real time: one in, one packet's worth of playback out.
                jitter.Add((ushort)packets, buffer[..size]); jitter.Read(output, start * 2, frame * 2);
            }
            Require(jitter.Lost == 0, $"No packet lost at {bitrate} bit/s, {frame / 16} ms");
            var decoded = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(output.AsSpan());
            return (HighBand(decoded[(rate / 2)..^(rate / 2)].ToArray()) / HighBand(source[(rate / 2)..^(rate / 2)]), (bytes + packets * 50) * 8.0 / seconds / 1000, packets);
        }
        var before = Run(8000, 320, 2); var after = Run(12000, 960, 10);
        var cost = 0.0;
        foreach (var (bitrate, frame, complexity) in new[] { (12000, 960, 10) })
        {
            using var probe = OpusCodecFactory.CreateEncoder(rate, 1, OpusApplication.OPUS_APPLICATION_VOIP); probe.Bitrate = bitrate; probe.Complexity = complexity; probe.UseInbandFEC = true; probe.PacketLossPercent = 5;
            var scratch = new byte[1275]; for (var start = 0; start + frame <= rate; start += frame) probe.Encode(source.AsSpan(start, frame), frame, scratch.AsSpan(), scratch.Length);
            cost = double.MaxValue;
            for (var pass = 0; pass < 4; pass++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (var start = 0; start + frame <= source.Length; start += frame) probe.Encode(source.AsSpan(start, frame), frame, scratch.AsSpan(), scratch.Length);
                if (pass > 0) cost = Math.Min(cost, watch.Elapsed.TotalMilliseconds / seconds / 10);
            }
        }
        Console.WriteLine($"Economy voice: upper band kept {before.High:P0} → {after.High:P0}; on the wire {before.WireKbps:F1} → {after.WireKbps:F1} kbit/s ({before.Packets / seconds} → {after.Packets / seconds} packets/s); encoder {cost:F2}% of one core");
        Require(cost < 4, "The stronger encoder stays light on the CPU");
        Require(after.High > before.High * 1.5 && after.High > 0.25, "Economy keeps the upper speech band (4–7 kHz) far better than before");
        Require(after.WireKbps < before.WireKbps * 0.8, "Economy sends less over the wire than before, packet headers included");
        // A lost 60 ms packet is concealed for 60 ms, so playback timing does not drift.
        using (var encoder = OpusCodecFactory.CreateEncoder(rate, 1, OpusApplication.OPUS_APPLICATION_VOIP))
        using (var jitter = new VoiceJitterBuffer())
        {
            encoder.Bitrate = 12000; encoder.UseInbandFEC = true; encoder.PacketLossPercent = 10; var buffer = new byte[1275];
            for (ushort n = 0; n < 6; n++) { var size = encoder.Encode(source.AsSpan(n * 960, 960), 960, buffer.AsSpan(), buffer.Length); if (n != 2) jitter.Add(n, buffer[..size]); }
            var pcm = new byte[960 * 2]; for (var i = 0; i < 5; i++) jitter.Read(pcm, 0, pcm.Length);
            Require(jitter.Lost == 1 && jitter.Recovered == 1, "A lost 60 ms packet is rebuilt from FEC");
        }
    }
    private static void CheckAvatars()
    {
        var folder = Path.Combine(Path.GetTempPath(), "VoiceconAvatars-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            const string me = "00112233445566778899aabbccddeeff", friend = "ffeeddccbbaa99887766554433221100", peer = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
            Avatars.Load(me, folder);
            var big = Path.Combine(folder, "big.jpg"); File.WriteAllBytes(big, new byte[Avatars.MaxPhotoFileBytes + 1]);
            var refused = false; try { Avatars.LoadPhoto(big).Dispose(); } catch (IOException) { refused = true; }
            Require(refused, "A photo over the size limit is refused");
            var junk = Path.Combine(folder, "junk.png"); File.WriteAllText(junk, "not an image"); refused = false; try { Avatars.LoadPhoto(junk).Dispose(); } catch (IOException) { refused = true; }
            Require(refused, "A file that is not a picture is refused");
            var wide = Path.Combine(folder, "wide.png");
            using (var picture = new Bitmap(800, 400)) { using (var g = Graphics.FromImage(picture)) { g.Clear(Color.Red); g.FillRectangle(Brushes.Blue, 200, 0, 400, 400); } picture.Save(wide, System.Drawing.Imaging.ImageFormat.Png); }
            var photo = Avatars.LoadPhoto(wide);
            Require(photo.Width == Avatars.PhotoSize && photo.Height == Avatars.PhotoSize && photo.GetPixel(Avatars.PhotoSize / 2, Avatars.PhotoSize / 2).B > 200 && photo.GetPixel(2, 2).B > 200, "A wide photo is cropped to its centre square");
            var shared = Avatars.Share(photo);
            Require(shared.Length < Avatars.MaxSharedBytes && Avatars.Decode(shared) is { Width: Avatars.SharedSize }, "The shared photo is small and decodes");
            Require(Avatars.Decode(System.Text.Encoding.ASCII.GetBytes("GIF89a-not-allowed")) is null && Avatars.Decode([0xFF, 0xD8, 1, 2, 3, 4, 5, 6, 7]) is null, "Only real PNG or JPEG data from peers is decoded");
            Avatars.SetOwn(Avatars.Palette[3], photo);
            Avatars.SetFriend(friend, Avatars.Palette[5], null);
            Avatars.SetSession(peer, Avatars.Palette[1], null);
            Require(Avatars.Get(peer)?.Color == Avatars.Palette[1], "A group-call avatar shows during the call");
            Avatars.ClearSession(); Require(Avatars.Get(peer) is null, "Group-call avatars are not kept");
            Avatars.SetFriend(me, Avatars.Palette[0], null); Require(Avatars.Get(me)?.Color == Avatars.Palette[3], "Nobody can overwrite your own avatar");
            Avatars.Load(me, folder);
            Require(Avatars.Get(me) is { Photo: { Width: Avatars.PhotoSize } } own && own.Color == Avatars.Palette[3] && Avatars.Get(friend)?.Color == Avatars.Palette[5], "Own and friends' avatars survive restart");
            using (var avatar = new Bitmap(60, 60)) using (var g = Graphics.FromImage(avatar)) { g.Clear(Color.Black); Neu.Avatar(g, new RectangleF(5, 5, 50, 50), me, "Я"); Require(avatar.GetPixel(30, 30).B > 150, "The photo is drawn inside the avatar circle"); }
            Avatars.SetOwn(null, null); Avatars.SetFriend(friend, null, null);
            Require(Avatars.Get(me) is null && !File.Exists(Path.Combine(folder, me + ".png")), "Removing the photo deletes it");
        }
        finally { Avatars.Load("", Path.Combine(folder, "none")); try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
    /// <summary>Watched streams sit in the main window: large with the chat on the right, small above the chat, or below on a narrow window.</summary>
    private static void CheckStage()
    {
        // Layout on its own, at fixed sizes (a CI desktop may be smaller than the window asked for).
        using (var stagePanel = new Panel()) using (var chatPanel = new Panel()) using (var layout = new CenterLayout(stagePanel, chatPanel) { Size = new Size(1000, 600) })
        {
            layout.PerformLayout();
            Require(chatPanel.Bounds == layout.ClientRectangle, "Without streams the chat fills the middle");
            layout.Mode = StageMode.Full; layout.PerformLayout();
            Require(!layout.Narrow && stagePanel.Right <= chatPanel.Left && chatPanel.Width == layout.ChatColumnWidth && chatPanel.Height == 600 && stagePanel.Width > chatPanel.Width, "Watching: the stream is large and the chat becomes a column on the right");
            layout.Mode = StageMode.Mini; layout.PerformLayout();
            Require(stagePanel.Bottom <= chatPanel.Top && stagePanel.Width == 1000 && chatPanel.Height > stagePanel.Height, "Collapsed: a mini view above the chat");
            layout.Mode = StageMode.Full; layout.Size = new Size(700, 600); layout.PerformLayout();
            Require(layout.Narrow && stagePanel.Bottom <= chatPanel.Top && chatPanel.Height > 100 && stagePanel.Width == 700, "On a narrow window the chat goes below the stream");
            layout.Mode = StageMode.Hidden; layout.PerformLayout();
            Require(chatPanel.Bounds == layout.ClientRectangle, "With nothing to watch the chat fills the middle again");
        }
        // The window: watching shows the stage, collapsing and the last stream ending put things back.
        using var form = new MainForm(true, "graphite"); _ = form.Handle; Prepare(form); form.PerformLayout();
        var center = Get<CenterLayout>(form, "center"); var stage = Get<StageView>(form, "stage");
        Require(center.Mode == StageMode.Hidden, "No stage before watching");
        Invoke(form, "ShowPreviewStage", false);
        Require(center.Mode == StageMode.Full && stage.Mode == StageMode.Full && stage.Count == 2 && stage.Focused == "preview:1", "Watching opens the stage with the chosen stream large");
        Invoke(form, "SetStage", StageMode.Mini); Require(center.Mode == StageMode.Mini && stage.Mode == StageMode.Mini, "The stage collapses to the mini view");
        stage.Remove("preview:1"); Require(stage.Focused == "preview:2", "Removing the shown stream shows the next one");
        stage.Remove("preview:2"); Invoke(form, "SetStage", StageMode.Full);
        Require(center.Mode == StageMode.Hidden, "With nothing left to watch the stage goes away");
        var added = new Bitmap(32, 18); stage.ShowFrame("missing", added); var disposed = false; try { _ = added.Width; } catch (ArgumentException) { disposed = true; }
        Require(disposed, "A frame for a stream no longer shown is released");
    }
    private static void CheckChatSelection()
    {
        using var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(520, 400) };
        var view = new ChatView { Location = Point.Empty, Size = new Size(500, 380) }; host.Controls.Add(view); _ = host.Handle; _ = view.Handle;
        var states = new Dictionary<string, MessageState> { ["a"] = MessageState.Read, ["b"] = MessageState.Failed };
        view.StateOf = id => states.GetValueOrDefault(id);
        view.SetRecords([new ChatRecord("a", "Вы", "Первое сообщение, его можно выделить частично", true, DateTimeOffset.Now.AddMinutes(-2)), new ChatRecord("c", "Алекс", "Ответ", false, DateTimeOffset.Now.AddMinutes(-1)), new ChatRecord("b", "Вы", "Не ушло", true, DateTimeOffset.Now)]);
        var items = (System.Collections.IList)typeof(ChatView).GetField("items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var first = items.Cast<object>().First(i => (bool)i.GetType().GetProperty("Day")!.GetValue(i)! == false);
        var text = (RectangleF)first.GetType().GetProperty("TextBounds")!.GetValue(first)!;
        var offset = (float)typeof(ChatView).GetProperty("Offset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var press = new Point((int)(text.X + 6), (int)(text.Y + offset + text.Height / 2));
        typeof(ChatView).GetMethod("BeginSelect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [first, press]);
        var selector = (TextBox)typeof(ChatView).GetField("selector", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        bool Selecting() => typeof(ChatView).GetField("selecting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view) is not null;
        // The test window is never shown, so "visible" is checked through the view's own state.
        Require(Selecting() && selector.Text == "Первое сообщение, его можно выделить частично" && selector.ReadOnly && Rectangle.Round(new RectangleF(text.X, text.Y + offset, text.Width, text.Height)) == selector.Bounds, "Pressing a message lays a read-only selection box exactly over its text");
        selector.Select(7, 9); Require(selector.SelectedText == "сообщение", "Part of a message can be selected");
        view.SetRecords([]); Require(!Selecting() && selector.SelectionLength == 0, "The selection box goes away when the chat changes");
        using var frame = new Bitmap(view.Width, view.Height);
        view.SetRecords([new ChatRecord("a", "Вы", "Прочитано", true, DateTimeOffset.Now)]); view.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
        var item = items.Cast<object>().First(i => (bool)i.GetType().GetProperty("Day")!.GetValue(i)! == false);
        var bubble = (RectangleF)item.GetType().GetProperty("Bounds")!.GetValue(item)!; offset = (float)typeof(ChatView).GetProperty("Offset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var tickPixels = 0; for (var y = (int)(bubble.Bottom + offset - 22); y < bubble.Bottom + offset - 4; y++) for (var x = (int)(bubble.Right - 30); x < bubble.Right - 4; x++) { var c = frame.GetPixel(x, y); if (c.G > 190 && c.B > 220 && c.R < 170) tickPixels++; }
        Require(tickPixels > 6, "Read messages show coloured ticks");
    }
    /// <summary>A link or other word wider than a bubble is split over lines; copying gives it back whole.</summary>
    private static void CheckLongWords()
    {
        const string link = "https://www.faceit.com/ru/matchmaking/f4148ddd-bce8-41b8-9131-ee83afcdd6dd/Europe%205v5%20Queue";
        var message = "Заходи " + link + " быстрее";
        using var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(440, 400) };
        var view = new ChatView { Location = Point.Empty, Size = new Size(420, 380) }; host.Controls.Add(view); _ = host.Handle; _ = view.Handle;
        view.SetRecords([new ChatRecord("a", "max19", message, false, DateTimeOffset.Now.AddMinutes(-1)), new ChatRecord("b", "Вы", link, true, DateTimeOffset.Now)]);
        var items = ((System.Collections.IList)typeof(ChatView).GetField("items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!).Cast<object>()
            .Where(i => (bool)i.GetType().GetProperty("Day")!.GetValue(i)! == false).ToList();
        foreach (var item in items)
        {
            var bubble = (RectangleF)item.GetType().GetProperty("Bounds")!.GetValue(item)!;
            var shown = (string)item.GetType().GetProperty("Text")!.GetValue(item)!;
            Require(bubble.Left >= 0 && bubble.Right <= view.Width && bubble.Width <= view.Width * 0.8f, $"A long link stays inside its bubble ({bubble.Left:0}..{bubble.Right:0} of {view.Width})");
            Require(shown.Contains('\n') && shown.Split('\n', ' ').All(word => TextRenderer.MeasureText(word, ChatView.MessageFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width <= bubble.Width), "A long link is split into pieces that fit a line");
        }
        var first = items[0]; var text = (RectangleF)first.GetType().GetProperty("TextBounds")!.GetValue(first)!;
        var offset = (float)typeof(ChatView).GetProperty("Offset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        typeof(ChatView).GetMethod("BeginSelect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [first, new Point((int)text.X + 4, (int)(text.Y + offset + 6))]);
        var selector = Get<TextBox>(view, "selector"); selector.SelectAll();
        string Selected() => (string)typeof(ChatView).GetMethod("SelectedText", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [])!;
        Require(Selected() == message, "Copying a selected split link gives the original text back");
        var at = selector.Text.IndexOf("matchmaking", StringComparison.Ordinal); var stop = selector.Text.IndexOf("Queue", StringComparison.Ordinal) + 5;
        selector.Select(at, stop - at); Require(Selected() == link[link.IndexOf("matchmaking", StringComparison.Ordinal)..], "Copying part of a split link leaves out the added line breaks");
        var (plain, none) = ((string, int[]))typeof(ChatView).GetMethod("BreakLongWords", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["Обычное сообщение\nв две строки", ChatView.MessageFont, 200])!;
        Require(plain == "Обычное сообщение\nв две строки" && none.Length == 0, "Ordinary words are not split");
        var (emoji, _) = ((string, int[]))typeof(ChatView).GetMethod("BreakLongWords", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [string.Concat(Enumerable.Repeat("😀", 60)), ChatView.MessageFont, 120])!;
        Require(emoji.Split('\n').All(line => line.Length > 0 && !char.IsHighSurrogate(line[^1]) && !char.IsLowSurrogate(line[0])), "Emoji are never cut in half");
    }
    /// <summary>Clicking your avatar opens its style picker; the settings page no longer has one.</summary>
    private static void CheckAvatarPopup()
    {
        using var popup = new AvatarPopup(); Color? chosen = Color.Empty; popup.ColorChosen += c => chosen = c;
        popup.Set("ffeeddccbbaa99887766554433221100", "Игрок", Avatars.Palette[2], hasPhoto: false);
        var remove = Get<SoftButton>(popup, "remove"); var colors = Get<AvatarColorPicker>(popup, "colors");
        Require(!remove.Enabled, "Without a photo there is nothing to remove");
        Require(Get<int>(colors, "selected") == 2, "The picker shows the chosen colour");
        typeof(AvatarColorPicker).GetMethod("Choose", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(colors, [4]);
        Require(chosen?.ToArgb() == Avatars.Palette[4].ToArgb(), "Choosing a colour in the picker reports it");
        popup.Set("ffeeddccbbaa99887766554433221100", "Игрок", null, hasPhoto: true); Require(remove.Enabled && Get<int>(colors, "selected") == -1, "With a photo it can be removed; automatic colour shows as such");
        using var form = new MainForm(true, "graphite"); _ = form.Handle;
        var card = Get<SelfCard>(form, "selfCard"); var clicked = 0; card.AvatarClicked += () => clicked++;
        card.Size = new Size(240, 48); var center = Point.Round(new PointF(card.AvatarBounds.X + card.AvatarBounds.Width / 2, card.AvatarBounds.Y + card.AvatarBounds.Height / 2));
        typeof(SelfCard).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(card, [new MouseEventArgs(MouseButtons.Left, 1, center.X, center.Y, 0)]);
        typeof(SelfCard).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(card, [new MouseEventArgs(MouseButtons.Left, 1, center.X, center.Y, 0)]);
        Require(clicked == 1, "Clicking your avatar opens the picker");
        typeof(SelfCard).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(card, [new MouseEventArgs(MouseButtons.Left, 1, 200, center.Y, 0)]);
        typeof(SelfCard).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(card, [new MouseEventArgs(MouseButtons.Left, 1, 200, center.Y, 0)]);
        Require(clicked == 1, "Clicking your name does not");
        Require(form.GetType().GetField("avatarColors", BindingFlags.Instance | BindingFlags.NonPublic) is null, "Settings no longer carry the avatar picker");
    }
    /// <summary>Energy between 4 and 7.5 kHz (what narrowband voice loses), from 512-point DFT frames.</summary>
    private static double HighBand(short[] samples)
    {
        double total = 0;
        for (var start = 0; start + 512 <= samples.Length; start += 512)
            for (var k = 128; k < 240; k += 2)
            {
                double re = 0, im = 0; for (var n = 0; n < 512; n++) { var angle = 2 * Math.PI * k * n / 512; re += samples[start + n] * Math.Cos(angle); im -= samples[start + n] * Math.Sin(angle); }
                total += re * re + im * im;
            }
        return total;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
