using System.Text.Json.Nodes;
using Voicecon;
using System.Reflection;
using System.Collections.Concurrent;

internal static class FeatureChecks
{
    public static void CheckIdentityAndVideo()
    {
        using var profiles = new Profiles(); var a = profiles.A; var b = profiles.B;
        var alice = a.OwnCard("Алиса 😎", "https://ntfy.sh"); var bob = b.OwnCard("Боб", "https://ntfy.sh");
        Require(FriendCard.Decode(alice.Encode()) == alice, "Friend code roundtrip");
        a.Add(bob); b.Add(alice); var id = Guid.NewGuid().ToString("N");
        var sealedMessage = a.Seal(bob, CallSignal.Create("offer", id, "private-test-code"));
        var opened = b.Open(sealedMessage); Require(opened?.Friend.Id == alice.Id && opened?.Signal.Code == "private-test-code", "Encrypted invitation");
        var json = JsonNode.Parse(sealedMessage)!; var bytes = Convert.FromBase64String(json["C"]!.GetValue<string>()); bytes[0] ^= 1; json["C"] = Convert.ToBase64String(bytes);
        Require(b.Open(json.ToJsonString()) is null, "Tampered invitation must fail authentication");
        Require(a.Open(sealedMessage) is null, "Wrong recipient must fail authentication");
        Require(b.Open(a.Seal(bob, new CallSignal("offer", id, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120))) is null, "Expired invitation");
        Require(b.Open(a.Seal(bob, new CallSignal("offer", id, long.MinValue))) is null, "Malformed timestamp");
        using (var reload = new ContactBook(profiles.First)) Require(reload.Id == a.Id && reload.Friends.Count == 1, "DPAPI identity and contact persistence");
        using var bitmap = new Bitmap(1920, 1080); using (var graphics = Graphics.FromImage(bitmap)) { graphics.Clear(Color.CornflowerBlue); graphics.FillRectangle(Brushes.Gold, 300, 100, 500, 300); }
        var packet = VideoCodec.Encode(bitmap, 2, 123, 1280, 720); using var decoded = VideoCodec.Decode(packet);
        Require(decoded.Width == 1280 && decoded.Height == 720 && packet.Length <= 48000 && VideoCodec.Sequence(packet) == 123, "Bounded JPEG video roundtrip");
        var rejected = false; try { VideoCodec.Decode([1, 0, 0, 0, 0, 4, 5]); } catch (FormatException) { rejected = true; } Require(rejected, "Malformed video must be rejected");
        Require(UiMotion.IsIdle, "Motion timer is stopped while idle");
        var icon = BrandIcon.CreateFile(); Require(BitConverter.ToUInt16(icon, 4) == 7 && BitConverter.ToUInt16(icon, 6 + 6) == 32, "Full-color multiresolution icon");
        Console.WriteLine("PASS contact codes, ECDH/AES-GCM invitations, tamper/recipient/expiry rejection, DPAPI persistence, bounded 720p video codec, idle animation timer");
    }
    public static async Task CheckMailbox()
    {
        using var profiles = new Profiles(); var a = profiles.A; var b = profiles.B;
        var alice = a.OwnCard("Synthetic A", "https://ntfy.sh"); var bob = b.OwnCard("Synthetic B", "https://ntfy.sh"); a.Add(bob); b.Add(alice);
        using var first = new CallMailbox(a, alice); using var second = new CallMailbox(b, bob);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        second.Received += (_, signal) => completion.TrySetResult(signal.Code ?? ""); first.Start(); second.Start();
        var until = DateTime.UtcNow.AddSeconds(35);
        while (!first.Connected || !second.Connected) { if (DateTime.UtcNow > until) throw new TimeoutException("Public ntfy websocket could not connect"); await Task.Delay(100); }
        await first.SendAsync(bob, CallSignal.Create("offer", Guid.NewGuid().ToString("N"), "synthetic-encrypted-delivery-check"));
        var received = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); Require(received == "synthetic-encrypted-delivery-check", "Public delivery");
        Console.WriteLine("PASS public ntfy: two synthetic encrypted identities, websocket subscription and encrypted invitation delivery; cache disabled; no user contacts, device capture or audio used");
    }
    public static async Task CheckCallFlow()
    {
        using var profiles = new Profiles(); var a = profiles.A; var b = profiles.B;
        var alice = a.OwnCard("Synthetic A", "https://ntfy.sh"); var bob = b.OwnCard("Synthetic B", "https://ntfy.sh"); a.Add(bob); b.Add(alice);
        using var formA = new MainForm(true, "graphite"); using var formB = new MainForm(true, "graphite");
        using var mailA = new CallMailbox(a, alice); using var mailB = new CallMailbox(b, bob);
        Set(formA, "contacts", a); Set(formB, "contacts", b); Set(formA, "mailbox", mailA); Set(formB, "mailbox", mailB);
        Get<Settings>(formA, "settings").Name = "Synthetic A"; Get<Settings>(formB, "settings").Name = "Synthetic B";
        Invoke(formA, "RefreshFriends", new object?[] { null }); Invoke(formB, "RefreshFriends", new object?[] { null });
        var context = SynchronizationContext.Current!; var errors = new ConcurrentQueue<Exception>();
        mailA.Received += (friend, signal) => Post(formA, friend, signal); mailB.Received += (friend, signal) => Post(formB, friend, signal);
        mailA.Start(); mailB.Start(); await Wait(() => mailA.Connected && mailB.Connected, "mailboxes connected");
        await Call(formA, "CallFriendAsync"); await Wait(() => Has(formB, "incomingCall"), "incoming call delivered");
        await Call(formB, "AcceptIncomingAsync");
        await Wait(() => Get<VoiceRoom>(formA, "room").CanChat && Get<VoiceRoom>(formB, "room").CanChat, "accepted call connects");
        Get<TextBox>(formA, "chatInput").Text = "Full-flow chat 😎"; Invoke(formA, "SendMessage");
        await Wait(() => Get<List<VoiceRoom.ChatLine>>(formB, "chatHistory").Any(l => l.Text == "Full-flow chat 😎"), "main UI chat delivery");
        Invoke(formA, "EndCall"); await Wait(() => Get<VoiceRoom>(formB, "room").IsDisposed, "remote hangup");
        await Call(formA, "CallFriendAsync"); await Wait(() => Has(formB, "incomingCall"), "second incoming call"); Invoke(formB, "RejectIncoming");
        await Wait(() => !Has(formA, "outgoingCall") && !Has(formB, "incomingCall"), "rejected call cleans state");
        await Call(formA, "CallFriendAsync"); await Wait(() => Has(formB, "incomingCall"), "third incoming call"); Invoke(formA, "EndCall");
        await Wait(() => !Has(formB, "incomingCall"), "caller cancellation clears ringing");
        Require(!formA.Visible && !formB.Visible && UiMotion.IsIdle, "Integration test must remain hidden and idle");
        Console.WriteLine("PASS full application call flow through public encrypted delivery: mutual contacts → dial → incoming → accept → WebRTC → emoji chat → hangup; reject and caller cancellation; no visible windows, notification playback, microphone, desktop or camera capture");

        void Post(MainForm form, FriendCard friend, CallSignal signal) => context.Post(async _ =>
        { if (form.IsDisposed) return; try { await (Task)Invoke(form, "OnSignal", friend, signal)!; } catch (Exception ex) { errors.Enqueue(ex); } }, null);
        async Task Wait(Func<bool> predicate, string label)
        {
            var deadline = DateTime.UtcNow.AddSeconds(35);
            while (!predicate()) { if (errors.TryDequeue(out var error)) throw error; if (DateTime.UtcNow > deadline) throw new TimeoutException(label); await Task.Delay(50); }
        }
        static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
        static Task Call(object target, string name) => (Task)Invoke(target, name)!;
        static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        static bool Has(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target) is not null;
        static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }
    private static void Require(bool condition, string label) { if (!condition) throw new Exception(label); }
    private sealed class Profiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "VoiceconChecks-" + Guid.NewGuid().ToString("N"));
        public string First => Path.Combine(root, "a");
        public ContactBook A { get; }
        public ContactBook B { get; }
        public Profiles() { A = new ContactBook(First); B = new ContactBook(Path.Combine(root, "b")); }
        public void Dispose()
        {
            A.Dispose(); B.Dispose();
            var path = Path.GetFullPath(root); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(temp + "VoiceconChecks-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
