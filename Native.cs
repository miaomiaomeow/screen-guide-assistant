using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace ScreenGuide;

internal static class Native
{
    internal const int GwlExStyle = -20;
    internal const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    internal const int WmMouseActivate = 0x21, WmHotkey = 0x312;
    internal const uint SwpNoActivate = 0x10, SwpShowWindow = 0x40;
    internal static readonly nint HwndTopmost = new(-1);
    [StructLayout(LayoutKind.Sequential)] internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct GUITHREADINFO
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public RECT CaretRect;
    }
    internal delegate bool EnumProc(nint hwnd, nint lparam);
    internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint thread, uint time);
    internal delegate nint KeyboardProc(int code, nint message, nint data);
    internal delegate nint MouseProc(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, KeyboardProc callback, nint module, uint thread);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] internal static extern nint SetMouseHook(int id, MouseProc callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint lparam);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern bool GetPhysicalCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    internal static string ClassName(nint hwnd)
    {
        var s = new StringBuilder(256); GetClassName(hwnd, s, s.Capacity); return s.ToString();
    }
    internal static bool IsOwnWindow(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }
    internal static bool Usable(nint hwnd)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        return DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }
    internal static bool IsMenuPopup(nint hwnd)
    {
        var name = ClassName(hwnd);
        return name == "#32768" || name.Contains("Popup", StringComparison.OrdinalIgnoreCase);
    }
    internal static nint RootTarget(nint hwnd)
    {
        if (hwnd == 0) return 0;
        var root = GetAncestor(hwnd, 2); if (root == 0) root = hwnd;
        for (var i = 0; i < 8 && IsMenuPopup(root); i++)
        {
            var owner = GetWindow(root, 4);
            if (owner == 0 && TryMenuState(GetWindowThreadProcessId(root, out _), out var menu)) owner = menu.MenuOwner;
            if (owner == 0 || owner == root) break;
            root = GetAncestor(owner, 2); if (root == 0) root = owner;
        }
        return root;
    }
    internal static bool CanTarget(nint hwnd) => Usable(hwnd) && !IsOwnWindow(hwnd) &&
        ClassName(hwnd) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    internal static bool IsTargetForeground(nint foreground, nint target) => RootTarget(foreground) == target;
    internal static string AppName(nint hwnd)
    {
        try { GetWindowThreadProcessId(hwnd, out var pid); return Process.GetProcessById((int)pid).ProcessName; }
        catch { return "未知软件"; }
    }
    internal static List<nint> OwnedPopups(nint target)
    {
        var result = new List<nint>();
        EnumWindows((h, _) => { if (h != target && Usable(h) && IsMenuPopup(h) && BelongsTo(h, target)) result.Add(h); return true; }, 0);
        return result;
    }
    internal static bool BelongsTo(nint hwnd, nint target)
    {
        if (HasWindowOwner(hwnd, target)) return true;
        if (!IsMenuPopup(hwnd)) return false;
        var popupThread = GetWindowThreadProcessId(hwnd, out var popupProcess);
        var targetThread = GetWindowThreadProcessId(target, out var targetProcess);
        return TryMenuState(targetThread, out var menu) && MenuAssociation(menu.Flags,
            HasWindowOwner(menu.MenuOwner, target), targetProcess, popupProcess, targetThread, popupThread);
    }
    private static bool HasWindowOwner(nint hwnd, nint target)
    {
        if (hwnd == 0 || target == 0) return false;
        if (hwnd == target || GetAncestor(hwnd, 2) == target || GetAncestor(hwnd, 3) == target) return true;
        // Separate windows in the same process remain separate targets.
        int depth = 0;
        for (var owner = GetWindow(hwnd, 4); owner != 0 && depth++ < 32; owner = GetWindow(owner, 4))
            if (owner == target) return true;
        return false;
    }
    internal static bool TryMenuState(uint thread, out GUITHREADINFO info)
    {
        info = new() { Size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) && (info.Flags & 0x1C) != 0 && info.MenuOwner != 0;
    }
    internal static bool MenuAssociation(uint flags, bool ownerBelongs, uint targetProcess, uint popupProcess, uint targetThread, uint popupThread) =>
        (flags & 0x1C) != 0 && ownerBelongs && targetProcess != 0 && targetProcess == popupProcess &&
        targetThread != 0 && targetThread == popupThread;
    internal static bool PointIsTarget(Rect bounds, nint target)
    {
        return BelongsTo(PointWindow(bounds), target);
    }
    internal static nint PointWindow(Rect bounds) => WindowFromPoint(new POINT { X = (int)(bounds.X + bounds.Width/2), Y = (int)(bounds.Y + bounds.Height/2) });
    internal static bool SameRect(Rect a, Rect b) => Math.Abs(a.X-b.X)<1 && Math.Abs(a.Y-b.Y)<1 && Math.Abs(a.Width-b.Width)<1 && Math.Abs(a.Height-b.Height)<1;
}
