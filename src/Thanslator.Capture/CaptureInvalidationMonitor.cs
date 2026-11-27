using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Thanslator.Capture;

/// <summary>只监测前台/选区/滚动失效事件；不记录全局键盘输入。</summary>
public sealed class CaptureInvalidationMonitor : IDisposable
{
    private readonly WinEventProc _callback;
    private readonly List<nint> _hooks = [];
    private HwndSource? _window;
    private bool _rawInputRegistered;
    private bool _disposed;
    public event Action<nint, uint>? Changed;
    public event Action? Wheel;

    public CaptureInvalidationMonitor()
    {
        _callback = (_, kind, hwnd, objectId, _, _, _) =>
        {
            if (kind != 0x800B || objectId == 0) Changed?.Invoke(hwnd, kind);
        };
        try
        {
            foreach (var kind in new uint[] { 3, 0x12, 0x20, 0x8005, 0x800B, 0x8014 })
            {
                var hook = SetWinEventHook(kind, kind, 0, _callback, 0, 0, 2);
                if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                _hooks.Add(hook);
            }
            _window = new HwndSource(new HwndSourceParameters("Thanslator.CaptureEvents")
            { ParentWindow = new nint(-3), WindowStyle = 0, Width = 0, Height = 0 });
            _window.AddHook(WindowProc);
            var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 0x100, Target = _window.Handle };
            if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _rawInputRegistered = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x00FF) return 0;
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(lParam, 0x10000003, 0, ref size, headerSize) != 0 || size < headerSize + 8 || size > 4096) return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var read = GetRawInputData(lParam, 0x10000003, buffer, ref size, headerSize);
            if (read == uint.MaxValue || read < headerSize + 8) return 0;
            if (Marshal.ReadInt32(buffer) == 0 && (Marshal.ReadInt16(buffer, (int)headerSize + 4) & 0x0C00) != 0)
                Wheel?.Invoke();
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        if (_rawInputRegistered)
        {
            var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 1 };
            RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        }
        _window?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { public uint Type; public uint Size; public nint Device; public nint WParam; }
    private delegate void WinEventProc(nint hook, uint kind, nint hwnd, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
}
