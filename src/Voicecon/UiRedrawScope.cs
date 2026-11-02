using System.Runtime.InteropServices;

namespace Voicecon;

internal sealed class UiRedrawScope : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    private readonly Control control;
    private readonly bool active;
    public UiRedrawScope(Control control)
    {
        this.control = control; active = control.IsHandleCreated;
        if (active) SendMessage(control.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
    }
    public void Dispose()
    {
        if (active && !control.IsDisposed && control.IsHandleCreated) { SendMessage(control.Handle, 0x000B, new IntPtr(1), IntPtr.Zero); control.Invalidate(); }
    }
}
