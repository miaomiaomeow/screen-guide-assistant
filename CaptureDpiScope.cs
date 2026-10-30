using System.Runtime.InteropServices;

namespace ScreenGuide;

// Window bounds, DWM bounds and the desktop DC must be queried in the same units.
// Keep this scope synchronous: the previous context belongs to this exact thread.
internal readonly struct CaptureDpiScope : IDisposable
{
    private readonly nint _previous;
    private CaptureDpiScope(nint previous) => _previous = previous;
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    internal static CaptureDpiScope Enter()
    {
        var previous = SetThreadDpiAwarenessContext(new nint(-4)); // PER_MONITOR_AWARE_V2
        if (previous == 0) throw new InvalidOperationException("无法建立物理像素截图坐标，请重新启动指路助手。");
        return new(previous);
    }
    public void Dispose() { if (_previous != 0) SetThreadDpiAwarenessContext(_previous); }
}
