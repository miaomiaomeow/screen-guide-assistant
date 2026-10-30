using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenGuide;

internal sealed class OverlayWindow : Window
{
    private nint _hwnd;
    private readonly Border _border;
    internal OverlayWindow()
    {
        Title = "屏幕指路高亮";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Width = Height = 100;
        Content = _border = new Border { BorderBrush = ShellTheme.Accent, BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, IsHitTestVisible = false };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(_hwnd, Native.GwlExStyle).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GwlExStyle, new nint(style | Native.WsExTransparent | Native.WsExNoActivate | Native.WsExToolWindow));
            HwndSource.FromHwnd(_hwnd).AddHook((nint h, int msg, nint w, nint l, ref bool handled) =>
            {
                if (msg == Native.WmMouseActivate) { handled = true; return new nint(3); } // MA_NOACTIVATE
                if (msg == 0x84) { handled = true; return new nint(-1); } // HTTRANSPARENT
                return 0;
            });
        };
    }
    internal void Highlight(Rect physicalBounds, bool estimated = false)
    {
        _border.BorderBrush = estimated ? Brushes.White : ShellTheme.Accent;
        _border.BorderThickness = new Thickness(estimated ? 2 : 3);
        // Leave image-estimated interiors unchanged for local pixel validation.
        _border.Background = Brushes.Transparent;
        var r = physicalBounds; r.Inflate(4, 4);
        if (!IsVisible) Show();
        // UIA supplies physical pixels. SetWindowPos avoids WPF's DIP/monitor-origin conversion.
        Native.SetWindowPos(_hwnd, Native.HwndTopmost, (int)Math.Floor(r.X), (int)Math.Floor(r.Y),
            (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height), Native.SwpNoActivate | Native.SwpShowWindow);
        Log.Write("overlay", $"source={(estimated ? "image-estimate" : "uia")} rect={physicalBounds} style=0x{Native.GetWindowLongPtr(_hwnd, Native.GwlExStyle).ToInt64():X}");
    }
}
