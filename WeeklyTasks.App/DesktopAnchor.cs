using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace WeeklyTasks.App;

// Keep the normal WPF composition target intact. The shell owns this tool window,
// and it stays at desktop depth instead of reparenting its rendering surface.
public sealed class DesktopAnchor(Window window)
{
    private nint _window, _owner, _previousOwner, _previousExtended;
    private HwndSource? _source;
    private bool _attached, _settingDepth;
    public bool IsAttached => _attached && IsWindow(_owner) && IsWindow(_window) && GetWindowLongPtr(_window, -8) == _owner;
    public string LastError { get; private set; } = "";
    public Rect ScreenBounds { get { GetWindowRect(_window, out var r); return new Rect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top)); } }
    public double DpiScale => Math.Max(96, GetDpiForWindow(_window)) / 96.0;
    public static Point CursorPosition { get { GetCursorPos(out var p); return new Point(p.X, p.Y); } }
    public bool Attach()
    {
        _window = new WindowInteropHelper(window).Handle;
        if (_window == 0) return false;
        if (IsAttached) { KeepAtDesktopDepth(); return true; }
        if (_attached) Detach();
        _owner = FindWindow("Progman", null);
        if (_owner == 0) { LastError = "デスクトップが準備されていません。再接続を待っています。"; return false; }
        _previousOwner = GetWindowLongPtr(_window, -8);
        _previousExtended = GetWindowLongPtr(_window, -20);
        SetWindowLongPtr(_window, -8, _owner);
        if (GetWindowLongPtr(_window, -8) != _owner)
        { LastError = "デスクトップへ接続できません。再接続を待っています。"; return false; }
        SetWindowLongPtr(_window, -20, (nint)(((long)_previousExtended & ~0x40008L) | 0x80L));
        _attached = true; _source = HwndSource.FromHwnd(_window); _source?.AddHook(OnMessage);
        ShowWindow(_window, 4); KeepAtDesktopDepth(); LastError = ""; return true;
    }
    public void Detach()
    {
        if (!_attached) return;
        _attached = false; _source?.RemoveHook(OnMessage); _source = null;
        SetWindowLongPtr(_window, -8, IsWindow(_previousOwner) ? _previousOwner : 0);
        SetWindowLongPtr(_window, -20, _previousExtended); _owner = 0;
    }
    public void KeepAtDesktopDepth()
    {
        if (!IsAttached) return;
        // Owned windows remain above their owner; HWND_BOTTOM puts the group at the desktop.
        _settingDepth = true;
        try { SetWindowPos(_window, (nint)1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x40 | 0x200); }
        finally { _settingDepth = false; }
    }
    private nint OnMessage(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (_attached && !_settingDepth && message == 0x46) // WINDOWPOSCHANGING: normal clicks must not raise it above apps.
        { var p = Marshal.PtrToStructure<WindowPosition>(lparam); p.Flags |= 0x4 | 0x10; Marshal.StructureToPtr(p, lparam, false); }
        return 0;
    }
    public void Move(double x, double y) { if (IsAttached) SetWindowPos(_window, 0, (int)x, (int)y, 0, 0, 0x1 | 0x4 | 0x10); }
    public void SetEnabled(bool value) => window.IsEnabled = value;
    public void Dispose() { _source?.RemoveHook(OnMessage); _source = null; }
    public object Status()
    {
        var r = ScreenBounds;
        return new { attached = IsAttached, ownerClass = ClassName(_owner), childStyle = ((long)GetWindowLongPtr(_window, -16) & 0x40000000L) != 0,
            topmostStyle = ((long)GetWindowLongPtr(_window, -20) & 8L) != 0, visible = IsWindowVisible(_window),
            x = r.X, y = r.Y, width = r.Width, height = r.Height, renderer = "WPF desktop-owned tool window", error = LastError };
    }
    private static string ClassName(nint h) { var s = new StringBuilder(128); if (h != 0) GetClassName(h, s, s.Capacity); return s.ToString(); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition { public nint Window, After; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint h, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out NativeRect r);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint p);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int command);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint h);
}
