using System.Runtime.InteropServices;

namespace Thanslator.App.Interop;

internal static class NativeWindowStyles
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private static readonly nint HwndTopmost = new(-1);

    public static void ApplyOverlayStyles(nint hwnd, bool clickThrough, bool show = true)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style |= WsExTopmost | WsExToolWindow | WsExNoActivate;
        if (clickThrough)
        {
            style |= WsExTransparent;
        }
        else
        {
            style &= ~WsExTransparent;
        }

        SetWindowLongPtr(hwnd, GwlExStyle, new nint(style));
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged | (show ? SwpShowWindow : 0));
    }

    public static void Place(nint hwnd, Thanslator.Core.RectD bounds) =>
        SetWindowPos(hwnd, HwndTopmost, (int)bounds.Left, (int)bounds.Top,
            (int)bounds.Width, (int)bounds.Height, SwpNoActivate);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TryActivate(nint hwnd);

    public static void Move(nint hwnd, int x, int y)
    {
        SetWindowPos(hwnd, HwndTopmost, x, y, 0, 0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    public static double GetDpiScale(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    public static bool TryGetWindowRect(nint hwnd, out int left, out int top, out int right, out int bottom)
    {
        if (GetWindowRect(hwnd, out var rect))
        {
            left = rect.Left;
            top = rect.Top;
            right = rect.Right;
            bottom = rect.Bottom;
            return true;
        }

        left = top = right = bottom = 0;
        return false;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
