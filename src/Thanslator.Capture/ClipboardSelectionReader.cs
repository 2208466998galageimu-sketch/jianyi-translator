using System.Diagnostics;
using System.Runtime.InteropServices;
using Thanslator.Core;

namespace Thanslator.Capture;

/// <summary>
/// 通用兜底取词：向当前前台窗口发送 Ctrl+C / Ctrl+Insert，轮询剪贴板里的选区文本，
/// 备份并尽力恢复可安全复制的格式；遇到未知格式或并发复制就停止。
/// 只在用户开启 ClipboardFallback 且统一隐私检查允许时调用。
/// </summary>
public sealed class ClipboardSelectionReader
{
    private const uint CfUnicodeText = 13;
    private const uint CfText = 1;
    private const uint GmemMoveable = 0x0002;
    private const uint KeyEventFKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkC = 0x43;
    private const ushort VkInsert = 0x2D;
    private const uint InputKeyboard = 1;

    // 通用窗口各尝试一次；已知终端只发 Ctrl+Insert，避免中断任务。
    private const int FirstCopyTimeoutMs = 800;
    private const int InsertCopyTimeoutMs = 700;
    private const int PollIntervalMs = 20;

    public CaptureResult Capture(nint foregroundHwnd, CancellationToken cancellationToken)
    {
        var allowed = CaptureSecurity.CheckWindow(foregroundHwnd);
        if (allowed != CaptureStatus.Success) return new CaptureResult(allowed);
        if (!NativeMethods.GetCursorPos(out var cursor)) return new(CaptureStatus.Unsupported);
        var processName = GetProcessName(foregroundHwnd);
        using var owner = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Thanslator.Clipboard")
        {
            ParentWindow = new nint(-3), WindowStyle = 0, Width = 0, Height = 0
        });
        using var backup = BackUp(cancellationToken);
        if (backup is null) return new(CaptureStatus.ClipboardUnavailable);
        var expectedSequence = backup.Sequence;
        var expectedOwner = GetClipboardOwner();
        var copied = false;
        string? captured = null;
        var restored = true;
        try
        {
            var keys = ClipboardCapturePolicy.IsTerminal(processName)
                ? new[] { VkInsert } : new[] { VkC, VkInsert };
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CaptureSecurity.CheckWindow(foregroundHwnd) != CaptureStatus.Success ||
                    GetClipboardSequenceNumber() != expectedSequence) return new(CaptureStatus.Canceled);
                // 热键修饰键尚未松开时，不把 Ctrl+C 误发为 Ctrl+Shift+C。
                var releaseDeadline = Environment.TickCount64 + 300;
                while (ModifiersDown() && Environment.TickCount64 < releaseDeadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.Sleep(PollIntervalMs);
                }
                if (ModifiersDown() || !SendCopy(key)) return new(CaptureStatus.ClipboardUnavailable);
                var deadline = Environment.TickCount64 + (key == VkC ? FirstCopyTimeoutMs : InsertCopyTimeoutMs);
                while (Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(PollIntervalMs);
                    var sequence = GetClipboardSequenceNumber();
                    if (sequence == expectedSequence)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }
                    var clipboardOwner = GetClipboardOwner();
                    NativeMethods.GetWindowThreadProcessId(clipboardOwner, out var ownerPid);
                    NativeMethods.GetWindowThreadProcessId(foregroundHwnd, out var targetPid);
                    if (ownerPid == 0 || ownerPid != targetPid) return new(CaptureStatus.Canceled);
                    expectedSequence = sequence;
                    expectedOwner = clipboardOwner;
                    copied = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (NativeMethods.GetForegroundWindow() != foregroundHwnd) return new(CaptureStatus.Canceled);
                    if (!OpenClipboard(owner.Handle)) continue;
                    try
                    {
                        if (!ClipboardCapturePolicy.CanRestore(expectedSequence, expectedOwner,
                                GetClipboardSequenceNumber(), GetClipboardOwner())) return new(CaptureStatus.Canceled);
                        captured = ReadTextUnderOpen();
                    }
                    finally { CloseClipboard(); }
                    if (!string.IsNullOrWhiteSpace(captured)) break;
                }
                if (!string.IsNullOrWhiteSpace(captured)) break;
            }
        }
        finally
        {
            if (copied) restored = Restore(backup, expectedSequence, expectedOwner, owner.Handle);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!restored) return new(CaptureStatus.ClipboardUnavailable, WarningCode: "clipboard.restore_failed");
        if (NativeMethods.GetForegroundWindow() != foregroundHwnd) return new(CaptureStatus.Canceled);
        if (string.IsNullOrWhiteSpace(captured)) return new(CaptureStatus.NoSelection);
        var snapshot = new SelectionSnapshot(captured.Trim(), [new RectD(cursor.X, cursor.Y, 1, 1)], foregroundHwnd,
            processName, SelectionCaptureMethod.Clipboard, Environment.TickCount64, HasPreciseGeometry: false);
        return new(CaptureStatus.Success, snapshot, true);
    }

    private sealed class Backup(uint sequence) : IDisposable
    {
        public uint Sequence { get; } = sequence;
        public Dictionary<uint, nint> Handles { get; } = [];
        public void Dispose()
        {
            foreach (var handle in Handles.Values) if (handle != 0) GlobalFree(handle);
        }
    }

    private static Backup? BackUp(CancellationToken cancellationToken)
    {
        if (!OpenClipboard(0)) return null;
        var backup = new Backup(GetClipboardSequenceNumber());
        var complete = false;
        try
        {
            uint format = 0;
            long totalBytes = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = new System.Text.StringBuilder(256);
                GetClipboardFormatName(format, name, name.Capacity);
                if (!ClipboardCapturePolicy.CanBackUp(format, name.ToString())) return null;
                var original = GetClipboardData(format);
                var size = GlobalSize(original);
                if (size == 0 || size > 32 * 1024 * 1024) return null;
                totalBytes += (long)size;
                if (totalBytes > 32 * 1024 * 1024) return null;
                var source = GlobalLock(original);
                if (source == 0) return null;
                try
                {
                    var bytes = new byte[(int)size];
                    try
                    {
                        Marshal.Copy(source, bytes, 0, bytes.Length);
                        var handle = GlobalAlloc(GmemMoveable, size);
                        if (handle == 0) return null;
                        backup.Handles.Add(format, handle);
                        var target = GlobalLock(handle);
                        if (target == 0) return null;
                        try { Marshal.Copy(bytes, 0, target, bytes.Length); }
                        finally { GlobalUnlock(handle); }
                    }
                    finally { Array.Clear(bytes); }
                }
                finally { GlobalUnlock(original); }
            }
            if (Marshal.GetLastWin32Error() != 0) return null;
            complete = true;
            return backup;
        }
        finally
        {
            CloseClipboard();
            if (!complete) backup.Dispose();
        }
    }

    private static bool Restore(Backup backup, uint expectedSequence, nint expectedOwner, nint owner)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!OpenClipboard(owner)) { Thread.Sleep(PollIntervalMs); continue; }
            try
            {
                // 用户或其他程序已经复制新内容时，绝不清空它。
                if (!ClipboardCapturePolicy.CanRestore(expectedSequence, expectedOwner,
                        GetClipboardSequenceNumber(), GetClipboardOwner())) return false;
                if (!EmptyClipboard()) return false;
                var restored = true;
                foreach (var format in backup.Handles.Keys.ToArray())
                {
                    if (SetClipboardData(format, backup.Handles[format]) == 0) restored = false;
                    else backup.Handles[format] = 0; // 成功后句柄所有权转给系统。
                }
                return restored;
            }
            finally { CloseClipboard(); }
        }
        return false;
    }

    private static string? ReadTextUnderOpen()
    {
        foreach (var format in new[] { CfUnicodeText, CfText })
        {
            var handle = GetClipboardData(format);
            if (handle == 0) continue;
            var pointer = GlobalLock(handle);
            if (pointer == 0) continue;
            try { return format == CfUnicodeText ? Marshal.PtrToStringUni(pointer) : Marshal.PtrToStringAnsi(pointer); }
            finally { GlobalUnlock(handle); }
        }
        return null;
    }

    private static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static bool SendCopy(ushort key) =>
        SendInput(4, [Key(VkControl, false), Key(key, false), Key(key, true), Key(VkControl, true)], Marshal.SizeOf<INPUT>()) == 4;

    private static INPUT Key(ushort vk, bool up) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = vk, Flags = up ? KeyEventFKeyUp : 0 } }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")]
    private static extern nint GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClipboardFormatName(uint format, System.Text.StringBuilder name, int length);
    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(nint memory);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    private static string GetProcessName(nint hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            return processId == 0
                ? "unknown"
                : Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
