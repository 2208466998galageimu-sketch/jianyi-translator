namespace Thanslator.Core;

public enum SurfaceMode
{
    Tooltip,
    Panel
}

public enum PopperDockEdge
{
    None,
    Left,
    Right,
    Top,
    Bottom
}

public enum SentenceTranslationMode
{
    LocalMt,
    LlmApi,
    LlmLocal
}

public sealed record LlmConnectionSettings(string Address, string Model);

public enum LocalMtModelStatus
{
    NotInstalled,
    Downloading,
    Ready,
    Error
}

/// <summary>
/// User-facing settings persisted in SQLite. New properties should have a
/// sensible default; the settings repository writes every known key.
/// </summary>
public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public bool LiteralMode { get; set; }
    public bool IncludePhrases { get; set; }
    public SurfaceMode SurfaceMode { get; set; } = SurfaceMode.Tooltip;
    public int DwellMs { get; set; } = 400;
    public int PanelAutoCloseMs { get; set; }
    public string SourceLang { get; set; } = "auto";
    public string TargetLang { get; set; } = "zh-Hans";
    public bool OfflineMode { get; set; }
    public SentenceTranslationMode TranslationMode { get; set; }
    public LlmConnectionSettings LlmApi { get; set; } = new("", "");
    public LlmConnectionSettings LlmLocal { get; set; } = new("http://127.0.0.1:11434/v1", "");
    public string[] KnowledgeLibraryIds { get; set; } = [];
    public bool LocalMtEnabled { get; set; }
    public string? LocalMtModelId { get; set; }
    public string? LocalMtModelPath { get; set; }
    public string? ModelDownloadDirectory { get; set; }
    public string? OllamaRuntimePath { get; set; }
    public string? ManagedLlmStorageRoot { get; set; }
    public int LocalMtThreads { get; set; } = 4;
    public LocalMtModelStatus LocalMtModelStatus { get; set; } = LocalMtModelStatus.NotInstalled;
    public bool AllowUnofficialProviders { get; set; }
    public bool ClipboardFallback { get; set; }
    public int OcrShortWords { get; set; } = 10;
    public int OcrMediumWords { get; set; } = 50;
    public int OcrShortSeconds { get; set; } = 90;
    public int OcrMediumSeconds { get; set; } = 300;
    public int OcrLongSeconds { get; set; } = 600;
    public bool OcrEnabled { get; set; }
    public string? OcrComponentPath { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+D";
    public string Theme { get; set; } = "system";
    public bool LaunchAtStartup { get; set; }
    public bool HistoryEnabled { get; set; }
    public string UiLocale { get; set; } = "zh-Hans";
    public double? PopperLeft { get; set; }
    public double? PopperTop { get; set; }
    public PopperDockEdge PopperDockEdge { get; set; } = PopperDockEdge.None;
    public double PopperDockOffset { get; set; }
    public bool PopperDrawerOpen { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
