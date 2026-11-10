using System.Runtime.InteropServices;
namespace Voicecon;
public static class WindowTheme
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private static void Set(IntPtr handle, int attribute, int value)
    {
        try { DwmSetWindowAttribute(handle, attribute, ref value, 4); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
    }
    private static int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;
    public static void Apply(Form form)
    {
        if (!form.IsHandleCreated) return;
        var t = UiTheme.Current;
        // Dark title bar, caption and border in the theme colours, rounded corners on Windows 11.
        Set(form.Handle, 20, t.Dark ? 1 : 0); Set(form.Handle, 34, ColorRef(UiTheme.Blend(t.Background, t.Accent, 0.25f)));
        Set(form.Handle, 35, ColorRef(t.Background)); Set(form.Handle, 36, ColorRef(t.Text)); Set(form.Handle, 33, 2);
    }
    /// <summary>Soft rounded corners and system shadow for popup menus on Windows 11 (ignored elsewhere).</summary>
    public static void RoundPopup(IntPtr handle) { Set(handle, 33, 3); Set(handle, 34, ColorRef(UiTheme.Current.Border)); }
}
public class ThemedDialog : Form
{
    public ThemedDialog() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); WindowTheme.Apply(this); }
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(e.Graphics, this, ClientRectangle);
}
