using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace Thanslator.App.Interop;

/// <summary>
/// 直接调用 <c>Shell_NotifyIcon</c> 的托盘图标。
/// 为什么不用 WinForms 的 NotifyIcon：它把 <c>Shell_NotifyIcon</c> 的失败吞掉，
/// 图标没出现时既没有异常也没有日志（2026-11-08 实际踩到过一次）。这里保留错误码，
/// 并且自己处理 <c>TaskbarCreated</c>，Explorer 重启后能重新挂上。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const int WsExToolWindow = 0x00000080;

    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private readonly Action<string>? _log;
    private HwndSource? _source;
    private bool _added;
    private bool _disposed;

    public TrayIcon(string tooltip, nint icon, Action? leftClick, Action? rightClick, Action<string>? log)
    {
        Tooltip = tooltip;
        _icon = icon;
        LeftClick = leftClick;
        RightClick = rightClick;
        _log = log;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    public string Tooltip { get; }

    public Action? LeftClick { get; }

    public Action? RightClick { get; }

    public bool IsAdded => _added;

    public void Add()
    {
        if (_disposed)
        {
            return;
        }

        _source ??= CreateMessageWindow();
        if (_log is not null)
        {
            LogEnvironment();
        }

        var data = CreateData(NifMessage | NifIcon | NifTip);
        Marshal.SetLastPInvokeError(0);
        _added = Shell_NotifyIcon(NimAdd, ref data);
        if (!_added)
        {
            var error = Marshal.GetLastWin32Error();
            _log?.Invoke($"tray: Shell_NotifyIcon(NIM_ADD) failed err={error} hwnd={data.WindowHandle} icon={data.IconHandle} id={data.Id}");
            // 图标句柄无效时也会失败：去掉 NIF_ICON 再试一次，用于区分「环境拒绝」和「图标参数问题」。
            var probe = CreateData(NifMessage | NifTip);
            Marshal.SetLastPInvokeError(0);
            var probeOk = Shell_NotifyIcon(NimAdd, ref probe);
            _log?.Invoke($"tray: probe without icon ok={probeOk} err={Marshal.GetLastWin32Error()}");
            if (probeOk)
            {
                // 去掉图标能加进去，说明只是图标句柄/参数问题；此时托盘里没有可见图标，
                // 不能把 IsAdded 置真，否则调用方会以为「隐藏到托盘」后还能找回窗口。
                var cleanup = CreateData(0);
                Shell_NotifyIcon(NimDelete, ref cleanup);
            }

            return;
        }

        _log?.Invoke($"tray: icon added hwnd={_source.Handle} tip={Tooltip}");
    }

    /// <summary>失败时最需要的上下文：窗口站、会话、完整性、shell 是否在同一桌面。</summary>
    private void LogEnvironment()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        GetWindowThreadProcessId(tray, out var shellPid);
        var station = GetProcessWindowStation();
        var name = new StringBuilder(256);
        GetUserObjectInformation(station, 2, name, name.Capacity * sizeof(char), out _);
        _log?.Invoke(
            $"tray: env session={Process.GetCurrentProcess().SessionId} station={name} " +
            $"user={Environment.UserName} shell_tray={tray} shell_pid={shellPid} integrity={DescribeIntegrity()}");
    }

    private static string DescribeIntegrity()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var token))
        {
            return "unknown";
        }

        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            if (!GetTokenInformation(token, 25, buffer, 64, out _))
            {
                return "unknown";
            }

            var sid = Marshal.ReadIntPtr(buffer);
            var count = Marshal.ReadByte(sid, 1);
            var last = Marshal.ReadInt32(sid, 8 + (count - 1) * 4);
            // 0x1000 = Low：低完整性进程（AI 沙箱、受保护模式等）无法向 Explorer 注册托盘图标，
            // Shell_NotifyIcon 会返回 ERROR_ACCESS_DENIED(5)。直接打印名字，省得再按字节反推。
            var name = last switch
            {
                0x0000 => "Untrusted",
                0x1000 => "Low",
                0x2000 => "Medium",
                0x2100 => "MediumPlus",
                0x3000 => "High",
                0x4000 => "System",
                _ => "Unknown"
            };
            return $"{name}(0x{last:X})";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(token);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added && _source is not null)
        {
            var data = CreateData(0);
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        _source?.Dispose();
        _source = null;
    }

    private HwndSource CreateMessageWindow()
    {
        // 不能用 HWND_MESSAGE 消息窗口：Shell_NotifyIcon 需要普通顶层窗口，
        // 消息窗口会被拒绝（2026-11-08 实测 NIM_ADD 返回 ERROR_ACCESS_DENIED）。
        var parameters = new HwndSourceParameters("Thanslator.Tray")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WsExToolWindow
        };
        var source = new HwndSource(parameters);
        source.AddHook(WndProc);
        return source;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
            switch (mouseMessage)
            {
                case WmLeftButtonUp:
                    LeftClick?.Invoke();
                    handled = true;
                    break;
                case WmRightButtonUp:
                    RightClick?.Invoke();
                    handled = true;
                    break;
            }
        }
        else if (_taskbarCreated != 0 && message == (int)_taskbarCreated)
        {
            // Explorer 重启后会广播这个消息，之前的图标全部失效，需要重新添加。
            _added = false;
            Add();
        }

        return nint.Zero;
    }

    private NotifyIconData CreateData(uint flags)
    {
        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _source?.Handle ?? nint.Zero,
            Id = 1,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            IconHandle = _icon,
            Tip = Tooltip
        };
        return data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIconHandle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder buffer, int length, out int needed);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int infoClass, nint buffer, int length, out int needed);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
