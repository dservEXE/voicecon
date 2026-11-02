using System.Runtime.InteropServices;
namespace Voicecon;
public static class WindowTheme
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public static void Apply(Form form)
    {
        if (!form.IsHandleCreated) return;
        var t = UiTheme.Current;
        void Set(int attr, int value) { try { DwmSetWindowAttribute(form.Handle, attr, ref value, 4); } catch (DllNotFoundException) { } }
        int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;
        Set(20, t.Id == "porcelain" ? 0 : 1); Set(34, ColorRef(t.Border)); Set(35, ColorRef(t.Background)); Set(36, ColorRef(t.Text)); Set(33, 2);
    }
}
public class ThemedDialog : Form
{
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); WindowTheme.Apply(this); }
}
