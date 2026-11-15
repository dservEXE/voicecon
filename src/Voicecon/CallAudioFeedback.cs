namespace Voicecon;
/// <summary>Call feedback follows real call state, independently of UI repaints.</summary>
public sealed class CallAudioFeedback(UiSounds sounds)
{
    private string? dialAttempt, completedAttempt; private long lastTone; private int connections;
    public void Update(string? calling, int connected, bool enabled, int device, long now)
    {
        var joined = connected > connections; connections = connected;
        if (joined) { completedAttempt = calling; StopDial(); if (enabled) sounds.PlayConnected(device); }
        if (!enabled || calling is null || calling == completedAttempt) { StopDial(); return; }
        // The ringback loops by itself; it is only restarted if something else took the ring slot.
        if (dialAttempt != calling || now - lastTone >= 3200 && !sounds.Ringing)
        { dialAttempt = calling; lastTone = now; sounds.PlayDial(device); }
    }
    private void StopDial() { if (dialAttempt is null) return; sounds.StopRing(); dialAttempt = null; }
}
