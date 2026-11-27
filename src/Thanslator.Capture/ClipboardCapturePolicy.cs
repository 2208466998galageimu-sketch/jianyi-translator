namespace Thanslator.Capture;

/// <summary>仅自动备份可复制的 HGLOBAL 格式；其他格式在发送复制前拒绝。</summary>
public static class ClipboardCapturePolicy
{
    public static bool CanBackUp(uint format, string? name) =>
        format is 1 or 7 or 8 or 13 or 15 or 16 or 17 ||
        name is "HTML Format" or "Rich Text Format" or "Rich Text Format Without Objects" or "PNG" or "JFIF";

    public static bool IsTerminal(string name) => name.ToLowerInvariant() is
        "windowsterminal" or "openconsole" or "conhost" or "cmd" or "powershell" or "pwsh" or "mintty" or "wezterm-gui" or "alacritty";

    public static bool CanRestore(uint expectedSequence, nint expectedOwner, uint currentSequence, nint currentOwner) =>
        expectedSequence == currentSequence && expectedOwner == currentOwner;
}
