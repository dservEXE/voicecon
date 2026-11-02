using System.Runtime.InteropServices;
namespace Voicecon;
public sealed partial class MainForm
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private readonly System.Windows.Forms.Timer keyPoll = new() { Interval = 15 };
    private readonly bool[] heldKeys = new bool[5];
    private Keys[] DesiredKeys => [(Keys)settings.MicrophoneHotkey, (Keys)settings.HeadphonesHotkey, (Keys)settings.OpenWindowHotkey, (Keys)settings.ScreenHotkey, (Keys)settings.CameraHotkey];
    private static bool Polled(Keys key) => (key & Keys.KeyCode) is Keys.MButton or Keys.XButton1 or Keys.XButton2 || (key & Keys.Modifiers) == 0 && (key & Keys.KeyCode) is not (>= Keys.F1 and <= Keys.F24);
    private bool TryRegister(Keys[] keys)
    {
        var success = true;
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i] == Keys.None || Polled(keys[i])) continue;
            uint modifiers = 0x4000; if (keys[i].HasFlag(Keys.Control)) modifiers |= 2; if (keys[i].HasFlag(Keys.Shift)) modifiers |= 4; if (keys[i].HasFlag(Keys.Alt)) modifiers |= 1;
            if (!RegisterHotKey(Handle, i + 1, modifiers, (uint)(keys[i] & Keys.KeyCode))) success = false;
        }
        return success;
    }
    private bool IsChordDown(Keys key)
    {
        bool Down(int code) => (GetAsyncKeyState(code) & 0x8000) != 0;
        return key != Keys.None && Down((int)(key & Keys.KeyCode)) && Down(0x11) == key.HasFlag(Keys.Control) && Down(0x10) == key.HasFlag(Keys.Shift) && Down(0x12) == key.HasFlag(Keys.Alt);
    }
    private void PollKeys(object? sender, EventArgs e)
    {
        var editing = ContainsFocus && ActiveInput(this);
        for (var i = 0; i < registeredKeys.Length; i++)
        {
            var key = registeredKeys[i]; if (key == Keys.None || !Polled(key)) continue;
            var down = IsChordDown(key); var edge = down && !heldKeys[i]; heldKeys[i] = down;
            if (edge && !(editing && (key & Keys.KeyCode) is not (Keys.MButton or Keys.XButton1 or Keys.XButton2))) ExecuteShortcut(i + 1);
        }
    }
    private static bool ActiveInput(Control root) => root is TextBoxBase { Focused: true } or HotkeyBox { Focused: true } || root.Controls.Cast<Control>().Any(ActiveInput);
    private void RegisterShortcuts()
    {
        if (previewMode || !IsHandleCreated) return;
        UnregisterShortcuts(); registeredKeys = DesiredKeys;
        if (!TryRegister(registeredKeys)) SetHint("Одна из горячих клавиш занята. Её можно изменить в настройках.");
        for (var i = 0; i < registeredKeys.Length; i++) heldKeys[i] = IsChordDown(registeredKeys[i]);
        keyPoll.Tick -= PollKeys; keyPoll.Tick += PollKeys;
        if (registeredKeys.Any(k => k != Keys.None && Polled(k))) keyPoll.Start();
    }
    private void UnregisterShortcuts() { keyPoll.Stop(); if (IsHandleCreated && !previewMode) for (var i = 1; i <= 5; i++) UnregisterHotKey(Handle, i); }
    private void ChangeHotkey(HotkeyBox box, int index)
    {
        var old = DesiredKeys; var proposed = old.ToArray(); proposed[index] = box.Chord;
        if (!HotkeyBox.Valid(box.Chord) || proposed.Where(k => k != Keys.None).Distinct().Count() != proposed.Count(k => k != Keys.None))
        { box.Chord = old[index]; SetHint("Выберите разные клавиши. Можно G, MOUSE4/5 или комбинацию. ЛКМ и ПКМ недоступны."); return; }
        UnregisterShortcuts();
        if (!previewMode && !TryRegister(proposed)) { UnregisterShortcuts(); box.Chord = old[index]; SetHint("Эта комбинация уже занята другой программой."); return; }
        UnregisterShortcuts();
        settings.MicrophoneHotkey = (int)proposed[0]; settings.HeadphonesHotkey = (int)proposed[1]; settings.OpenWindowHotkey = (int)proposed[2]; settings.ScreenHotkey = (int)proposed[3]; settings.CameraHotkey = (int)proposed[4]; registeredKeys = proposed;
        UpdateShortcutHint(); SaveSettings(); if (!box.Capturing) RegisterShortcuts();
    }
    private void ExecuteShortcut(int action)
    { switch (action) { case 1: ToggleMute(); break; case 2: ToggleDeafen(); break; case 3: ShowWindow(); break; case 4: _ = ToggleScreenAsync(); break; case 5: _ = ToggleCameraAsync(); break; } }
    private void UpdateShortcutHint() => shortcutHint.Text = $"{HotkeyBox.Format((Keys)settings.MicrophoneHotkey)} · микрофон    {HotkeyBox.Format((Keys)settings.HeadphonesHotkey)} · наушники    {HotkeyBox.Format((Keys)settings.OpenWindowHotkey)} · открыть окно";
    protected override void WndProc(ref Message m) { if (m.Msg == 0x0312) ExecuteShortcut(m.WParam.ToInt32()); base.WndProc(ref m); }
}
