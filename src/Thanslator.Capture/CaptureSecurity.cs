using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Thanslator.Core;

namespace Thanslator.Capture;

/// <summary>所有捕获后端在读取前共用的前台、桌面与权限检查。</summary>
public static class CaptureSecurity
{
    private static readonly HashSet<string> DeniedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "KeePass", "KeePassXC", "1Password", "Bitwarden", "CredentialUIBroker", "LogonUI", "LockApp", "winlogon"
    };

    public static CaptureStatus CheckWindow(nint hwnd)
    {
        if (hwnd == 0 || NativeMethods.GetForegroundWindow() != hwnd) return CaptureStatus.Canceled;
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return CaptureStatus.Forbidden;
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, name, 512, out _) ||
                !name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase)) return CaptureStatus.Forbidden;
        }
        finally { CloseDesktop(desktop); }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (DeniedProcesses.Contains(process.ProcessName)) return CaptureStatus.Forbidden;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CaptureStatus.Forbidden;
        }

        var handle = OpenProcess(0x1000, false, pid);
        if (handle == 0) return CaptureStatus.Forbidden;
        try
        {
            if (!OpenProcessToken(handle, 8, out var token)) return CaptureStatus.Forbidden;
            try
            {
                return GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) && elevated == 0
                    ? CaptureStatus.Success : CaptureStatus.Forbidden;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, uint length, out uint needed);
    [DllImport("kernel32.dll")]
    private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll")]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")]
    private static extern bool GetTokenInformation(nint token, int kind, out int info, int length, out int needed);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
