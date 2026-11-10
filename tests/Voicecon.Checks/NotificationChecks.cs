using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAudio.Wave;
using Voicecon;

internal static class NotificationChecks
{
    private static void Require(bool condition, string description) { if (!condition) throw new Exception(description); }
    private static short[] Read(IWaveProvider source, int frames)
    {
        var buffer = Enumerable.Repeat((byte)0x7B, frames * 4 + 16).ToArray();
        Require(source.Read(buffer, 8, frames * 4) == frames * 4, "Persistent provider must return full buffers, including silence");
        Require(buffer.AsSpan(0, 8).ToArray().All(b => b == 0x7B) && buffer.AsSpan(buffer.Length - 8).ToArray().All(b => b == 0x7B), "Read must respect offset/count");
        return MemoryMarshal.Cast<byte, short>(buffer.AsSpan(8, frames * 4)).ToArray();
    }
    public static async Task Check()
    {
        var signatures = new HashSet<string>();
        foreach (var kind in Enum.GetValues<NotificationCue>().Where(k => k != NotificationCue.CallConnected))
        {
            var clip = UiSounds.Clip(kind); var data = clip.Samples.Span;
            Require(data.Length > 0 && data[0] == 0 && data[1] == 0 && data[^1] == 0 && data[^2] == 0, "Cue must start/end at zero");
            Require(data.ToArray().All(s => float.IsFinite(s) && Math.Abs(s) < 0.2f), "Cue amplitude must be bounded");
            Require(data[^960..].ToArray().All(s => s == 0), "Cue must have a silent tail");
            signatures.Add(Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(data))));
            var mixer = new NotificationMixer(); mixer.SetCue(clip); var played = Read(mixer, clip.Frames + 960);
            Require(played.Any(s => s != 0) && played[^1920..].All(s => s == 0), "Natural EOF must end in exact digital silence");
            Require(Read(mixer, 4800).All(s => s == 0), "Idle buffer must clear stale bytes");
        }
        Require(signatures.Count == Enum.GetValues<NotificationCue>().Length - 1, "Every action cue is distinct");
        var cut = new NotificationMixer(); cut.SetCue(UiSounds.Clip(NotificationCue.MicrophoneOn)); var before = Read(cut, 2200); cut.SetCue(null); var tail = Read(cut, 1200);
        Require(Math.Abs(tail[0] - before[^2]) < 1000 && tail[960..].All(s => s == 0), "Stop must fade a phase-continuous tail within 10 ms");
        var replace = new NotificationMixer(); replace.SetCue(UiSounds.Clip(NotificationCue.HeadphonesOn)); Read(replace, 3900);
        replace.SetCue(UiSounds.Clip(NotificationCue.MicrophoneOff)); var transition = Read(replace, 1200);
        Require(transition.Select(Math.Abs).Max() < 9000, "Crossfade must remain bounded");
        Console.WriteLine($"PASS {signatures.Count} distinct watery cues, smooth zero endpoints, silent tails, EOF clears reused buffers, phase-continuous stop, bounded replacement fade");

        var outputs = new List<FakeOutput>();
        using (var sounds = new UiSounds(_ => { var p = new FakeOutput(); outputs.Add(p); return p; }))
        {
            sounds.Play(true, -1); var current = outputs.Single(); var audible = 0;
            for (var i = 0; i < 2000; i++)
            {
                if ((i & 1) == 0) sounds.Play((i & 2) == 0, -1); else sounds.PlayHeadphones((i & 2) == 0, -1);
                if (Read(current.Provider!, 48).Any(s => s != 0)) audible++;
            }
            Require(outputs.Count == 1 && current.InitCount == 1 && current.PlayCount == 1 && current.StopCount == 0 && current.DisposeCount == 0, "Rapid toggles must not stop, reopen or reinitialise audio output");
            Require(audible > 1900, "Rapid toggles must keep producing audible PCM");
            Read(current.Provider!, 48000); sounds.Play(true, -1); Require(Read(current.Provider!, 7440).Any(s => s != 0), "Next cue after a burst must play immediately");
            sounds.PlayRing("soft", "", -1); sounds.PlayHeadphones(false, -1); sounds.StopCue(); Require(Read(current.Provider!, 4800).Any(s => s != 0), "Ringing and button cues must have independent slots on one output");
            sounds.StopRing(); Read(current.Provider!, 480); Require(Read(current.Provider!, 960).All(s => s == 0), "Stopping ring must fade then silence");
            var stale = current.SnapshotStopped(); current.Fail(); sounds.Play(true, -1); Require(outputs.Count == 2 && current.DisposeCount == 1, "Next cue must recover from a device fault");
            stale(); sounds.Play(false, -1); Require(outputs.Count == 2, "Delayed old completion must not stop the new output");
            sounds.PlayHeadphones(true, 2); Require(outputs.Count == 3 && outputs[1].DisposeCount == 1, "Selected output change must replace only the old output");
            sounds.Stop(); Require(outputs[^1].StopCount == 0, "Stopping a cue must not reset the hardware");
        }
        Require(outputs.All(p => p.DisposeCount == 1), "Outputs must be released once on shutdown or actual device change");
        var attempts = 0; FakeOutput? recovered = null;
        using (var sounds = new UiSounds(_ => { attempts++; return recovered = new FakeOutput { FailInit = attempts == 1 }; }))
        { sounds.Play(true, -1); sounds.Play(false, -1); Require(attempts == 2 && Read(recovered!.Provider!, 7440).Any(s => s != 0), "Failed initialisation must not permanently disable notifications"); }
        Console.WriteLine("PASS 2000 rapid toggles: 1 device open, 0 resets, next cue audible; ring isolation; device failure/init recovery; stale callback rejected; selected output change and disposal");

        FakeOutput? callOutput = null;
        using (var callSounds = new UiSounds(_ => callOutput = new FakeOutput()))
        {
            var feedback = new CallAudioFeedback(callSounds); feedback.Update("attempt-a", 0, true, -1, 0);
            Require(Read(callOutput!.Provider!, 48000).Any(s => s != 0), "Outgoing call starts a dial tone");
            Require(Read(callOutput.Provider!, 96000).All(s => s == 0), "Dial tone has a quiet interval");
            feedback.Update("attempt-a", 0, true, -1, 1000); Require(Read(callOutput.Provider!, 960).All(s => s == 0), "UI refresh does not restart dial tone");
            feedback.Update("attempt-a", 0, true, -1, 3200); Require(Read(callOutput.Provider!, 24000).Any(s => s != 0), "Dial repeats while waiting");
            feedback.Update(null, 0, true, -1, 3300); Read(callOutput.Provider!, 480); Require(Read(callOutput.Provider!, 960).All(s => s == 0), "Answer/cancel stops dial tone with fade");
            feedback.Update(null, 1, true, -1, 3500); Require(Read(callOutput.Provider!, 18720).Any(s => s != 0), "Established connection plays its own cue");
            Read(callOutput.Provider!, 960); feedback.Update(null, 1, true, -1, 3600); Require(Read(callOutput.Provider!, 20000).All(s => s == 0), "Roster/UI updates do not repeat connection cue");
            feedback.Update(null, 2, true, -1, 3700); Require(Read(callOutput.Provider!, 18720).Any(s => s != 0), "Another participant has a connection cue");
            feedback.Update("attempt-b", 2, true, -1, 4000); Require(Read(callOutput.Provider!, 24000).Any(s => s != 0), "Conference invitation has dial feedback");
            feedback.Update("attempt-b", 2, false, -1, 4200); Read(callOutput.Provider!, 480); Require(Read(callOutput.Provider!, 4800).All(s => s == 0), "Disabling action sounds stops outgoing feedback");
            callSounds.VolumePercent = 0; callSounds.PlayConnected(-1); Require(Read(callOutput.Provider!, 18720).All(s => s == 0), "Master volume includes action sounds");
            callSounds.VolumePercent = 300; callSounds.PlayConnected(-1); Require(Read(callOutput.Provider!, 18720).Any(s => s != 0), "Master volume can amplify action sounds");
            Require(callOutput.InitCount == 1 && callOutput.StopCount == 0, "Dial and connect reuse persistent output");
        }
        Console.WriteLine("PASS outgoing ringback repeat/stop, answer/cancel, connection and participant-join cues, deduplicated UI updates, sound preference, master mute/300%, single output");
        var concurrent = new NotificationMixer();
        await Task.WhenAll(Task.Run(() => { for (var i = 0; i < 4000; i++) concurrent.SetCue(UiSounds.Clip((NotificationCue)(i % 4))); }), Task.Run(() => { var pcm = new byte[3840]; for (var i = 0; i < 4000; i++) Require(concurrent.Read(pcm, 0, pcm.Length) == pcm.Length, "Concurrent audio reads"); }));
        Read(concurrent, 24000); concurrent.SetCue(UiSounds.Clip(NotificationCue.HeadphonesOn)); Require(Read(concurrent, 11520).Any(s => s != 0), "Cue after concurrent burst");
        var idle = new NotificationMixer(); var block = new byte[3840]; var watch = Stopwatch.StartNew(); for (var i = 0; i < 5000; i++) idle.Read(block, 0, block.Length); watch.Stop();
        Console.WriteLine($"PASS concurrent cue replacement + rendering, recovery after burst; idle provider: {watch.Elapsed.TotalMilliseconds:0.00} ms CPU work for 100 s of synthetic silence; no output devices opened");
    }
    private sealed class FakeOutput : IWavePlayer
    {
        public IWaveProvider? Provider; public bool FailInit; public int InitCount, PlayCount, StopCount, DisposeCount;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => Provider!.WaveFormat;
        public void Init(IWaveProvider provider) { InitCount++; if (FailInit) throw new IOException("Synthetic output error"); Provider = provider; }
        public void Play() { PlayCount++; PlaybackState = PlaybackState.Playing; }
        public void Pause() { PlaybackState = PlaybackState.Paused; }
        public void Stop() { StopCount++; PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Dispose() { DisposeCount++; }
        public void Fail() { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs(new IOException("Synthetic device disconnected"))); }
        public Action SnapshotStopped() { var handler = PlaybackStopped; return () => handler?.Invoke(this, new StoppedEventArgs()); }
    }
}
