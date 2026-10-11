using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace WeeklyTasks.App;

// The HWND belongs to this application; only its parent and position are changed.
// Attach above the shell's icon view, rather than behind the wallpaper, so buttons stay usable.
public sealed class DesktopHost(Window window)
{
    private nint _window;
    private nint _parent;
    private nint _originalStyle, _originalExtended;
    private bool _savedStyles;
    private const int Style = -16, ExtendedStyle = -20;
    private const long Child = 0x40000000L, Popup = 0x80000000L, AppWindow = 0x40000L, ToolWindow = 0x80L, TopMost = 0x8L;
    private const uint NoSize = 0x1, NoZOrder = 0x4, NoActivate = 0x10, FrameChanged = 0x20, ShowWindowFlag = 0x40;

    public bool IsAttached => _window != 0 && _parent != 0 && IsWindow(_window) && IsWindow(_parent) && GetParent(_window) == _parent;
    public string LastError { get; private set; } = "";
    public Rect ScreenBounds
    {
        get { GetWindowRect(_window, out var r); return new Rect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top)); }
    }
    public double DpiScale => Math.Max(96, GetDpiForWindow(_window)) / 96.0;
    public static Point CursorPosition
    { get { GetCursorPos(out var p); return new Point(p.X, p.Y); } }

    public bool Attach()
    {
        _window = new WindowInteropHelper(window).Handle;
        if (_window == 0 || !IsWindow(_window)) return false;
        if (IsAttached) return true;
        _parent = FindIconHost();
        if (_parent == 0) { LastError = "Windowsのデスクトップが見つかりません。準備でき次第、再接続します。"; return false; }
        var bounds = ScreenBounds;
        if (!_savedStyles)
        { _originalStyle = GetWindowLongPtr(_window, Style); _originalExtended = GetWindowLongPtr(_window, ExtendedStyle); _savedStyles = true; }
        var newStyle = ((long)GetWindowLongPtr(_window, Style) & ~Popup) | Child;
        var newExtended = ((long)GetWindowLongPtr(_window, ExtendedStyle) & ~(AppWindow | TopMost)) | ToolWindow;
        SetWindowLongPtr(_window, Style, (nint)newStyle);
        SetWindowLongPtr(_window, ExtendedStyle, (nint)newExtended);
        Marshal.SetLastPInvokeError(0);
        var previous = SetParent(_window, _parent);
        int error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
        {
            SetWindowLongPtr(_window, Style, _originalStyle); SetWindowLongPtr(_window, ExtendedStyle, _originalExtended);
            _parent = 0; LastError = "デスクトップに接続できませんでした（" + new Win32Exception(error).Message + "）。";
            return false;
        }
        var point = new NativePoint { X = (int)bounds.Left, Y = (int)bounds.Top };
        ScreenToClient(_parent, ref point);
        SetWindowPos(_window, 0, point.X, point.Y, (int)bounds.Width, (int)bounds.Height, NoActivate | FrameChanged | ShowWindowFlag);
        LastError = "";
        return IsAttached;
    }

    public void Detach()
    {
        if (!_savedStyles || _window == 0 || !IsWindow(_window)) return;
        var bounds = ScreenBounds;
        SetParent(_window, 0);
        SetWindowLongPtr(_window, Style, (nint)(((long)_originalStyle & ~Child) | Popup));
        SetWindowLongPtr(_window, ExtendedStyle, _originalExtended);
        SetWindowPos(_window, 0, (int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height, NoActivate | NoZOrder | FrameChanged);
        _parent = 0;
    }

    public void Move(double screenX, double screenY)
    {
        if (!IsAttached) return;
        var point = new NativePoint { X = (int)screenX, Y = (int)screenY };
        ScreenToClient(_parent, ref point);
        SetWindowPos(_window, 0, point.X, point.Y, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    public object Status()
    {
        _window = new WindowInteropHelper(window).Handle;
        GetWindowThreadProcessId(_parent, out uint processId);
        string processName = "";
        try { if (processId != 0) processName = Process.GetProcessById((int)processId).ProcessName; }
        catch (ArgumentException) { }
        var bounds = _window == 0 ? Rect.Empty : ScreenBounds;
        return new
        {
            attached = IsAttached, parentClass = ClassName(_parent), parentProcess = processName,
            childStyle = _window != 0 && ((long)GetWindowLongPtr(_window, Style) & Child) != 0,
            topmostStyle = _window != 0 && ((long)GetWindowLongPtr(_window, ExtendedStyle) & TopMost) != 0,
            visible = _window != 0 && IsWindowVisible(_window),
            x = bounds.IsEmpty ? 0 : bounds.Left, y = bounds.IsEmpty ? 0 : bounds.Top,
            width = bounds.IsEmpty ? 0 : bounds.Width, height = bounds.IsEmpty ? 0 : bounds.Height,
            dpi = _window == 0 ? 0 : GetDpiForWindow(_window), error = LastError
        };
    }

    private static nint FindIconHost()
    {
        nint found = 0;
        EnumWindows((top, _) =>
        {
            string name = ClassName(top);
            if (name is not ("Progman" or "WorkerW")) return true;
            if (FindWindowEx(top, 0, "SHELLDLL_DefView", null) != 0) { found = top; return false; }
            // On newer shells, the icon view can be one container below Progman.
            EnumChildWindows(top, (child, _) =>
            {
                if (ClassName(child) == "SHELLDLL_DefView") { found = GetParent(child); return false; }
                return true;
            }, 0);
            return found == 0;
        }, 0);
        return found;
    }
    private static string ClassName(nint handle)
    { var text = new StringBuilder(256); if (handle != 0) GetClassName(handle, text, text.Capacity); return text.ToString(); }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
