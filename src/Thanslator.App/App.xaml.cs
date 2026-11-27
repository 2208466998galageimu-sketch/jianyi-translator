using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Thanslator.App.Interop;
using Thanslator.Capture;
using Thanslator.Core;
using Thanslator.Dictionary;
using Thanslator.Storage;
using Thanslator.Translation;
using Drawing = System.Drawing;

namespace Thanslator.App;

public partial class App : System.Windows.Application
{
    private MoreSettingsWindow? _moreSettingsWindow;
    private readonly SemaphoreSlim _settingsOperation = new(1, 1);
    private readonly SemaphoreSlim _settingsWrites = new(1, 1);
    private MoreSettingsRepository? _moreSettingsRepository;
    private SettingsRepository? _repository;
    private AppSettings _settings = new();
    private PopperWindow? _popper;
    private OverlayWindow? _overlay;
    private readonly string _knowledgeSession = Guid.NewGuid().ToString("N");
    private ToastWindow? _toast;
    private TrayIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private DictionaryService? _dictionary;
    private LiteralGlosser? _glosser;
    private LocalMtProvider? _localMt;
    private readonly System.Net.Http.HttpClient _llmApiHttp = LlmTranslationClient.CreateHttpClient(local: false);
    private readonly System.Net.Http.HttpClient _llmLocalHttp = LlmTranslationClient.CreateHttpClient(local: true);
    private MouseHook? _mouseHook;
    private DispatcherTimer? _dwellTimer;
    private DispatcherTimer? _hideTimer;
    private readonly SelectionCaptureService _captureService = new();
    private readonly ScreenOcrCapture _ocrCapture = new();
    private CaptureResult? _pendingOcr;
    private SelectionSnapshot? _ocrEditingSnapshot;
    private OcrRangeWindow? _ocrRange;
    private bool _ocrInProgress;
    private nint _lastExternalWindow;
    private nint _manualOcrTarget;
    private CancellationTokenSource? _manualOcrCancellation;
    private RegionSelectionWindow? _regionSelection;
    private CancellationTokenSource? _captureCancellation;
    private CancellationTokenSource? _lookupCancellation;
    private CaptureInvalidationMonitor? _invalidationMonitor;
    private SelectionSnapshot? _hoverSnapshot;
    private string? _suppressedFingerprint;
    private int _mouseDownX;
    private int _mouseDownY;
    private int _dwellOriginX;
    private int _dwellOriginY;
    private int _captureGeneration;
    private int _lookupGeneration;
    private bool _pointerInsideSelection;
    private long? _lastManualOcrHintAtMs;

    /// <summary>
    /// 取词/触发链路的诊断开关：设置环境变量 THANSLATOR_TRACE=1，
    /// 或创建 data\trace.on 文件后，关键节点写入 data\logs\trigger.log。
    /// 只记录长度/坐标/结果码，不记录选中文本。
    /// </summary>
    private static readonly bool TraceEnabled = IsTraceEnabled();

    /// <summary>单实例互斥体：第二个实例唤醒已有实例后立即退出，避免双托盘图标/热键冲突。</summary>
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showPopperSignal;
    private Thread? _showPopperThread;

    private static bool IsTraceEnabled()
    {
        if (Environment.GetEnvironmentVariable("THANSLATOR_TRACE") == "1")
        {
            return true;
        }

        try
        {
            return System.IO.File.Exists(System.IO.Path.Combine(AppPaths.DataRoot, "trace.on"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void Trace(string message)
    {
        if (!TraceEnabled)
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.LogsDirectory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppPaths.LogsDirectory, "trigger.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 诊断日志失败不影响取词
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!TryAcquireSingleInstance())
        {
            // 已有实例在运行：唤醒它把控制条显示出来，本进程退出。
            StartupLog.Write("startup: another instance is running; signal sent");
            Shutdown(0);
            return;
        }

        try
        {
            StartupLog.Write("startup: begin");
            Trace($"startup: trace enabled={TraceEnabled} args={string.Join(' ', e.Args)}");
            _repository = new SettingsRepository();
            StartupLog.Write($"startup: repository at {_repository.DatabasePath}");
            await _repository.InitializeAsync();
            _settings = await _repository.LoadAsync();
            var knowledgeRepository = new KnowledgeRepository(_repository.DatabasePath);
            await knowledgeRepository.InitializeAsync();
            _moreSettingsRepository = new MoreSettingsRepository(_repository.DatabasePath);
            await _moreSettingsRepository.LoadAsync(_settings.KnowledgeLibraryIds);
            StartupLog.Write("startup: settings loaded");

            await MigrateLegacyModelPathAsync();

            _dictionary = new DictionaryService();
            _glosser = new LiteralGlosser(_dictionary);
            if (TraceEnabled)
            {
                UiaSelectionReader.Diagnostic = m => Trace(m);
            }
            _localMt = CreateLocalMtProvider();
            _overlay = new OverlayWindow();
            _overlay.Dismissed += OnOverlayDismissed;
            _overlay.ModelSettingsRequested += (_, _) => OpenLocalMtSettings();
            _overlay.RetryRequested += async snapshot =>
            {
                if(!_settings.Enabled)return;
                _ocrEditingSnapshot=snapshot;_hoverSnapshot=snapshot;
                await LookupAndShowAsync(snapshot,++_lookupGeneration);
            };
            _overlay.ManualOcrAnchor = () => _popper?.GetScreenBounds();
            _overlay.OcrEditing += snapshot => _ocrEditingSnapshot = snapshot;
            _overlay.OcrTextCorrected += async (snapshot, text) =>
            {
                if (!_settings.Enabled) return;
                CancelLookup();
                var corrected = snapshot with
                {
                    Text = text,
                    OcrTranslationText = text == snapshot.Text ? snapshot.OcrTranslationText : null
                };
                _ocrEditingSnapshot = corrected;
                _hoverSnapshot = corrected;
                await LookupAndShowAsync(corrected, ++_lookupGeneration);
            };
            CreateTimers();
            try
            {
                _invalidationMonitor = new CaptureInvalidationMonitor();
                _invalidationMonitor.Changed += (hwnd, kind) => _ = Dispatcher.InvokeAsync(() =>
                {
                    if (kind == 3 && hwnd != 0 && !IsOwnWindow(hwnd) &&
                        NativeMethods.GetForegroundWindow() == hwnd) _lastExternalWindow = hwnd;
                    if (_manualOcrCancellation is not null)
                    {
                        if (kind == 0x20 || (kind == 3 && hwnd != _manualOcrTarget && !IsOwnWindow(hwnd) &&
                            NativeMethods.GetForegroundWindow() == hwnd)) CancelManualOcr();
                        return;
                    }
                    if (_hoverSnapshot?.IsManualOcr == true && kind != 0x20) return;
                    if (!_settings.Enabled) return;
                    if (kind is 0x8005 or 0x8014)
                        Trace($"invalidation event=0x{kind:X} generation={_captureGeneration} capture={_captureCancellation is not null}");
                    if (kind == 0x20 || (kind == 3 && !IsOwnWindow(hwnd))) ResetHover(hideOverlay: true);
                    else if (kind == 0x8005 && !IsOwnWindow(hwnd) &&
                        NativeMethods.RootWindow(hwnd) == NativeMethods.GetForegroundWindow()) ResetHover(hideOverlay: true);
                    else if (kind == 0x8014 && !IsOwnWindow(hwnd) &&
                        NativeMethods.RootWindow(hwnd) == NativeMethods.GetForegroundWindow())
                    {
                        ResetHover(hideOverlay: true);
                        _ = CaptureSelectionAfterMouseUpAsync(false);
                    }
                    else if (_hoverSnapshot is { } current && (hwnd == current.ForegroundHwnd ||
                        NativeMethods.RootWindow(hwnd) == current.ForegroundHwnd))
                    {
                        ResetHover(hideOverlay: true);
                        if (kind == 0x8014) _ = CaptureSelectionAfterMouseUpAsync(false);
                    }
                });
                _invalidationMonitor.Wheel += () =>
                {
                    if (_hoverSnapshot?.IsManualOcr == true) return;
                    if (_overlay is { IsVisible: true, CurrentSurface: SurfaceMode.Panel } &&
                        NativeMethods.GetCursorPos(out var p) && _overlay.ContainsScreenPoint(p.X, p.Y)) return;
                    ResetHover(hideOverlay: true);
                };
            }
            catch (Exception)
            {
                StartupLog.Write("capture: invalidation events unavailable");
            }

            _popper = new PopperWindow(_settings, SaveSettingsAsync);
            _popper.HiddenToTray += (_, _) =>
                ShowToast("已隐藏到系统托盘：点击托盘图标，或再次运行简译即可恢复。");
            _popper.QuitRequested += (_, _) => Shutdown();
            _popper.MoreSettingsRequested += (_, _) => OpenMoreSettings();
            _popper.OcrSettingsRequested += (_, _) => OpenMoreSettings("models/ocr");
            _popper.LocalMtSettingsRequested += (_, _) => OpenLocalMtSettings();
            _popper.LoadTranslationModels = LoadQuickTranslationModelsAsync;
            _popper.SelectTranslationEngine = id => ApplyQuickTranslationAsync(async models =>
            {
                var model = models.FirstOrDefault(row => MoreSettingsKnowledge.Text(row, "id") == id)
                    ?? throw new ArgumentException("该配置已移除，请重新选择。");
                return await TranslationSelection.ActivateAsync(model, _settings, CancellationToken.None);
            });
            _popper.SelectTranslationLanguages = (source, target) => ApplyQuickTranslationAsync(models =>
                Task.FromResult(TranslationSelection.ChangeLanguages(_settings, models, source, target)));
            _popper.KnowledgeComboChanged += async id =>
            {
                try
                {
                    var state=await _moreSettingsRepository.LoadAsync([]);
                    if(id.Length>0&&!state.Document["presets"]!.AsArray().Any(row=>MoreSettingsKnowledge.Text(row,"id")==id&&!MoreSettingsKnowledge.Deleted(row!)))return;
                    state.Document["active"]=id;await _moreSettingsRepository.SaveAsync(state.Document,state.Revision);
                    ResetHover(hideOverlay:true);await RefreshTranslationContextAsync();
                }
                catch{ShowToast("组合切换失败，请重新选择。");await RefreshTranslationContextAsync();}
            };
            _popper.RegionOcrRequested += (_, _) => BeginManualOcr();
            _popper.IsVisibleChanged += (_, _) =>
            {
                if (!_popper.IsVisible && _manualOcrCancellation is null && _overlay.IsManualOcrVisible)
                    ResetHover(hideOverlay: true);
            };
            _popper.LocationChanged += (_, _) => _overlay.RepositionManualOcr();
            _popper.SizeChanged += (_, _) => _overlay.RepositionManualOcr();
            var initialForeground = NativeMethods.GetForegroundWindow();
            if (initialForeground != 0 && !IsOwnWindow(initialForeground)) _lastExternalWindow = initialForeground;
            _popper.Show();
            await RefreshTranslationContextAsync();

            if (_settings.Enabled)
            {
                EnsureMouseHook();
            }

            var trayReady = CreateTray();
            _popper.CanHideToTray = trayReady;
            // 托盘失败提示不能被通用状态覆盖：否则用户只看到「选中文本后悬停」，
            // 按下「—」后控制条消失、又没有托盘图标，就再也找不回来了。
            string readyStatus;
            if (!trayReady)
            {
                readyStatus = "系统托盘不可用（进程完整性受限）；控制条将保持显示";
            }
            else if (_dictionary.IsAvailable)
            {
                readyStatus = "选中文本后悬停";
            }
            else
            {
                readyStatus = "本地词典未安装；请先放入 dict-en-zh.sqlite";
            }

            _popper.SetStatus(readyStatus);
            StartupLog.Write($"startup: ready; dictionary={_dictionary.IsAvailable} tray={trayReady}");
        }
        catch (Exception ex)
        {
            StartupLog.Write($"startup: fatal: {ex}");
            System.Windows.MessageBox.Show(
                $"简译 启动失败：{ex.Message}",
                "简译",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void CreateTimers()
    {
        _dwellTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.DwellMs, 200, 1000))
        };
        _dwellTimer.Tick += OnDwellTick;

        _hideTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _hideTimer.Tick += OnHideTick;
    }

    private Task<KnowledgeMatches> MatchKnowledgeAsync(string text, CancellationToken token) =>
        _moreSettingsRepository!.MatchAsync(text, _settings.SourceLang, _settings.TargetLang, _settings.KnowledgeLibraryIds, token, _knowledgeSession + ":" + _lookupGeneration);

    private async void OpenMoreSettings(string page = "knowledge")
    {
        if (_overlay?.IsManualOcrVisible != true) ResetHover(hideOverlay: true);
        if (_moreSettingsWindow is not null)
        {
            _moreSettingsWindow.Activate();
            await _moreSettingsWindow.NavigateAsync(page);
            return;
        }
        _moreSettingsWindow = new MoreSettingsWindow(_settings, SaveLocalMtSettingsAsync, SaveSettingsAsync,
            async () => { ResetHover(hideOverlay: true); await RefreshTranslationContextAsync(); }, CommitDataChangeAsync, page, _settingsOperation);
        _moreSettingsWindow.Closed += (_, _) => _moreSettingsWindow = null;
        _moreSettingsWindow.Show();
    }

    private async Task<List<JsonObject>> LoadQuickTranslationModelsAsync()
    {
        if (!await _settingsOperation.WaitAsync(0))
            throw new ArgumentException("请等待更多设置中的操作完成。");
        try
        {
            return await TranslationSelection.LoadModelsAsync(_moreSettingsRepository!, _settings, CancellationToken.None);
        }
        finally { _settingsOperation.Release(); }
    }

    private async Task ApplyQuickTranslationAsync(Func<List<JsonObject>, Task<AppSettings>> prepare)
    {
        if (!await _settingsOperation.WaitAsync(0))
            throw new ArgumentException("请等待更多设置中的操作完成。");
        try
        {
            var models = await TranslationSelection.LoadModelsAsync(_moreSettingsRepository!, _settings, CancellationToken.None);
            var draft = await prepare(models);
            await SaveLocalMtSettingsAsync(draft, null);
            if (_moreSettingsWindow is not null)
            {
                try { await _moreSettingsWindow.PublishTranslationSelectionAsync(); }
                catch (Exception) { Trace("settings view: translation selection refresh unavailable"); }
            }
        }
        finally { _settingsOperation.Release(); }
    }

    private async Task RefreshTranslationContextAsync()
    {
        if(_popper is null||_moreSettingsRepository is null)return;
        var state=await _moreSettingsRepository.LoadAsync([]);
        _popper.SetTranslationContext(state.Document["presets"]!.AsArray().Where(row=>row is not null&&!MoreSettingsKnowledge.Deleted(row))
            .Select(row=>(MoreSettingsKnowledge.Text(row,"id"),MoreSettingsKnowledge.Text(row,"name"))),MoreSettingsKnowledge.Text(state.Document,"active"));
    }

    private async Task SaveSettingsAsync(AppSettings settings)
    {
        await _settingsWrites.WaitAsync();
        try
        {
            if (_repository is not null) await _repository.SaveAsync(settings);
            ApplyCaptureSettings(settings);
            await RefreshTranslationContextAsync();
        }
        finally { _settingsWrites.Release(); }
    }

    private void ApplyCaptureSettings(AppSettings settings)
    {
        ResetHover(hideOverlay: true);
        if (!settings.Enabled) CancelManualOcr();
        if (!settings.Enabled || !settings.OcrEnabled) _ocrCapture.Stop();
        if (_dwellTimer is not null)
            _dwellTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.DwellMs, 200, 1000));
        if (settings.Enabled) EnsureMouseHook();
        else StopMouseHook();
    }

    private async Task CommitDataChangeAsync(Func<Task<AppSettings>> commit)
    {
        await _settingsWrites.WaitAsync();
        try
        {
            var loaded = await commit();
            try
            {
                _settings.Enabled = loaded.Enabled;
                _settings.LiteralMode = loaded.LiteralMode;
                _settings.IncludePhrases = loaded.IncludePhrases;
                _settings.SurfaceMode = loaded.SurfaceMode;
                _settings.DwellMs = loaded.DwellMs;
                _settings.PanelAutoCloseMs = loaded.PanelAutoCloseMs;
                _settings.OfflineMode = loaded.OfflineMode;
                _settings.ClipboardFallback = loaded.ClipboardFallback;
                _settings.OcrEnabled = loaded.OcrEnabled;
                _settings.OcrShortWords = loaded.OcrShortWords;
                _settings.OcrMediumWords = loaded.OcrMediumWords;
                _settings.OcrShortSeconds = loaded.OcrShortSeconds;
                _settings.OcrMediumSeconds = loaded.OcrMediumSeconds;
                _settings.OcrLongSeconds = loaded.OcrLongSeconds;
                await ApplyCommittedModelSettingsAsync(loaded);
                _popper?.ApplyToControls();
                ApplyCaptureSettings(_settings);
            }
            catch (Exception)
            {
                Trace("settings committed: runtime reload failed");
                ShowToast("数据已保存，部分设置需重启后生效。");
            }
        }
        finally { _settingsWrites.Release(); }
    }

    private void EnsureMouseHook()
    {
        if (_mouseHook is not null)
        {
            return;
        }

        var hook = new MouseHook();
        MouseHook.Diagnostic = m => Trace($"input {m}");
        hook.MouseEvent += OnGlobalMouseEvent;
        if (!hook.Start())
        {
            hook.Dispose();
            _popper?.SetStatus("鼠标监听启动失败，请重启简译");
            StartupLog.Write("mouse poll failed to start");
            return;
        }

        _mouseHook = hook;
        StartupLog.Write("mouse poll started");
    }

    private void StopMouseHook()
    {
        if (_mouseHook is null)
        {
            return;
        }

        _mouseHook.MouseEvent -= OnGlobalMouseEvent;
        _mouseHook.Dispose();
        _mouseHook = null;
        StartupLog.Write("mouse poll stopped");
    }

    private void OnGlobalMouseEvent(object? sender, MouseHookEventArgs e)
    {
        if (e.Kind != MouseHookEventKind.Move)
        {
            Trace($"input {e.Kind} {e.X},{e.Y} enabled={_settings.Enabled}");
        }

        _ = Dispatcher.InvokeAsync(
            () => HandleMouseEvent(e),
            DispatcherPriority.Input);
    }

    private void HandleMouseEvent(MouseHookEventArgs e)
    {
        if (_manualOcrCancellation is not null)
        {
            if (e.Kind is MouseHookEventKind.Escape or MouseHookEventKind.Unavailable ||
                NativeMethods.GetForegroundWindow() != _manualOcrTarget) CancelManualOcr();
            return;
        }
        if (e.Kind == MouseHookEventKind.LeftDown)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != 0 && !IsOwnWindow(foreground)) _lastExternalWindow = foreground;
        }
        if (!_settings.Enabled)
        {
            return;
        }

        if (_hoverSnapshot?.IsManualOcr == true &&
            e.Kind is not (MouseHookEventKind.Escape or MouseHookEventKind.Unavailable)) return;
        switch (e.Kind)
        {
            case MouseHookEventKind.Move:
                HandleMouseMove(e.X, e.Y);
                break;
            case MouseHookEventKind.LeftDown:
                HandleLeftDown(e.X, e.Y);
                break;
            case MouseHookEventKind.LeftUp:
                HandleLeftUp(e.X, e.Y);
                break;
            case MouseHookEventKind.Wheel:
                ResetHover(hideOverlay: true);
                break;
            case MouseHookEventKind.Unavailable:
                ResetHover(hideOverlay: true);
                _popper?.SetStatus("当前桌面暂不可访问；恢复后请重新选择文字");
                break;
            case MouseHookEventKind.Escape:
                _captureGeneration++;
                _captureCancellation?.Cancel();
                _dwellTimer?.Stop();
                OnOverlayDismissed(this, EventArgs.Empty);
                _overlay?.HideOverlay();
                break;
        }
    }

    private void BeginManualOcr()
    {
        if (_popper is null) return;
        if (!_settings.Enabled)
        {
            _popper.SetStatus("请先开启简译");
            return;
        }
        if (_manualOcrCancellation is not null)
        {
            ShowToast("正在框选或识别，请等待完成");
            return;
        }
        var component = _settings.OcrComponentPath ?? OcrHostClient.DefaultDirectory;
        if (!OcrHostClient.IsConfigured(component))
        {
            OpenMoreSettings("models/ocr");
            return;
        }
        var current = NativeMethods.GetForegroundWindow();
        var target = IsOwnWindow(current) ? _lastExternalWindow : current;
        if (target == 0 || (current != target && !NativeWindowStyles.TryActivate(target)) ||
            CaptureSecurity.CheckWindow(target) != CaptureStatus.Success ||
            ScreenOcrCapture.GetRegionBounds(target) is not { } area)
        {
            _popper.SetStatus("请先切到要识别的窗口，再点击框选翻译");
            return;
        }
        ResetHover(hideOverlay: true);
        EnsureMouseHook();
        if (_mouseHook is null) return;

        _manualOcrTarget = target;
        var request = new CancellationTokenSource();
        _manualOcrCancellation = request;
        _popper.Hide();
        var picker = new RegionSelectionWindow(area);
        _regionSelection = picker;
        picker.Closed += (_, _) =>
        {
            _regionSelection = null;
            _ = Dispatcher.InvokeAsync(() => FinishManualOcrAsync(picker.SelectedRegion, area, target, component, request),
                DispatcherPriority.Background);
        };
        picker.Show();
    }

    private async Task FinishManualOcrAsync(RectD? region, RectD area, nint target, string component,
        CancellationTokenSource request)
    {
        var popper = _popper!;
        SelectionSnapshot? snapshot = null;
        try
        {
            if (request.IsCancellationRequested)
            {
                if (NativeMethods.GetForegroundWindow() != target) ShowToast("目标窗口已切换，本次框选已取消");
                return;
            }
            if (region is null) return;
            if (ScreenOcrCapture.GetRegionBounds(target) != area)
            {
                ShowToast("目标窗口位置或大小已变化，请重新框选");
                return;
            }
            if (!ScreenOcrCapture.IsValidRegion(region.Value, area))
            {
                popper.SetStatus("请选择 8×8 至 3840×2160 像素的区域");
                ShowToast("框选范围需在 8×8 至 3840×2160 像素内，请重新选择");
                return;
            }
            var privacy = await _captureService.CheckOcrRegionAsync(target, region.Value, request.Token);
            var result = privacy.Status == CaptureStatus.Success
                ? await _ocrCapture.CaptureRegionAsync(target, region.Value, component, request.Token)
                : privacy;
            if (request.IsCancellationRequested || NativeMethods.GetForegroundWindow() != target)
            {
                ShowToast("识别已取消；请保持目标窗口在前台后重试");
                return;
            }
            if (result is { Status: CaptureStatus.Success, Snapshot: { } recognized })
            {
                snapshot = recognized;
            }
            else
            {
                OnCaptureFailed(result);
                ShowToast(result.Status switch
                {
                    CaptureStatus.Busy => "识别器正在忙，请稍后重试",
                    CaptureStatus.Timeout => "识别超时，请缩小区域后重试",
                    CaptureStatus.OcrUnavailable => "本地 OCR 组件不可用，请检查组件目录",
                    CaptureStatus.Canceled => "目标窗口状态已变化，请重新框选",
                    CaptureStatus.OcrNoText => "框选区域未识别到文字",
                    CaptureStatus.OcrOccluded => "框选区域被其他窗口遮挡",
                    CaptureStatus.Forbidden => "该区域无法安全读取",
                    _ => "框选识别未完成，请重试"
                });
            }
        }
        catch (OperationCanceledException)
        {
            ShowToast("本次识别已取消");
        }
        finally
        {
            _manualOcrCancellation = null;
            _manualOcrTarget = 0;
            request.Dispose();
            if (!Dispatcher.HasShutdownStarted) popper.Show();
        }

        if (snapshot is null || !_settings.Enabled || NativeMethods.GetForegroundWindow() != target) return;
        _hoverSnapshot = snapshot;
        await LookupAndShowAsync(snapshot, ++_lookupGeneration);
    }

    private void CancelManualOcr()
    {
        _manualOcrCancellation?.Cancel();
        _regionSelection?.Close();
    }

    private void HandleLeftDown(int x, int y)
    {
        _mouseDownX = x;
        _mouseDownY = y;
        _dwellTimer?.Stop();
        _hideTimer?.Stop();

        if (IsOverOwnUi(x, y))
        {
            // Panel: clicking its controls must not destroy the selection cache.
            if (_overlay?.CurrentSurface == SurfaceMode.Tooltip &&
                _overlay.IsVisible &&
                !_overlay.ContainsScreenPoint(x, y))
            {
                _overlay.HideOverlay();
            }

            return;
        }

        if (_overlay?.IsVisible == true)
        {
            _overlay.HideOverlay();
        }

        _captureGeneration++;
        _captureCancellation?.Cancel();
        CancelLookup();
        _hoverSnapshot = null;
        _suppressedFingerprint = null;
    }

    private void HandleLeftUp(int x, int y)
    {
        if (IsOverOwnUi(x, y))
        {
            return;
        }

        var dragged = Math.Abs(x - _mouseDownX) > 4 || Math.Abs(y - _mouseDownY) > 4;
        Trace($"leftup {x},{y} dragged={dragged}");
        // Capture even for a click: double-click word selection has no drag.
        _ = CaptureSelectionAfterMouseUpAsync(dragged);
    }

    private async Task CaptureSelectionAfterMouseUpAsync(bool dragged)
    {
        if (_hoverSnapshot?.IsManualOcr == true) return;
        var generation = ++_captureGeneration;
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0 || IsOwnWindow(hwnd)) return;

        await Task.Delay(dragged ? 120 : 180);
        if (generation != _captureGeneration || NativeMethods.GetForegroundWindow() != hwnd)
        {
            return;
        }

        var clipboardFallback = _settings.ClipboardFallback;
        var captured = await CaptureSelectionAsync(hwnd, clipboardFallback);
        var snapshot = captured.Status == CaptureStatus.Success ? captured.Snapshot : null;
        var pendingOcr = _settings.OcrEnabled && OcrCapturePolicy.CanFallback(captured, false);
        if (pendingOcr) snapshot ??= captured.Snapshot;

        if (generation != _captureGeneration || snapshot is null)
        {
            if (generation == _captureGeneration && snapshot is null && NativeMethods.GetForegroundWindow() == hwnd)
            {
                OnCaptureFailed(captured, dragged);
            }

            Trace($"capture dropped status={captured.Status} generation={generation}/{_captureGeneration} snapshot={(snapshot is null ? "null" : "ok")}");
            return;
        }

        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            Trace("capture dropped: foreground window changed");
            return;
        }

        Trace($"capture ok len={snapshot.Text.Length} rects={snapshot.LineRects.Count} bounds={snapshot.Bounds.Left:F0},{snapshot.Bounds.Top:F0},{snapshot.Bounds.Width:F0}x{snapshot.Bounds.Height:F0} app={snapshot.ProcessName}");

        await Dispatcher.InvokeAsync(() =>
        {
            if (generation != _captureGeneration)
            {
                return;
            }

            _hoverSnapshot = snapshot;
            _pendingOcr = pendingOcr ? captured : null;
            _suppressedFingerprint = null;
            if (NativeMethods.GetCursorPos(out var cursor) &&
                snapshot.HasPreciseGeometry && snapshot.ContainsPoint(cursor.X, cursor.Y, 8))
            {
                _pointerInsideSelection = true;
                Trace($"cursor {cursor.X},{cursor.Y} inside selection -> dwell");
                StartDwell(cursor.X, cursor.Y);
            }
            else
            {
                Trace($"cursor outside selection (bounds {snapshot.Bounds.Left:F0},{snapshot.Bounds.Top:F0} {snapshot.Bounds.Width:F0}x{snapshot.Bounds.Height:F0})");
            }
        });
    }

    /// <summary>UIA 优先，失败后走剪贴板（需显式开启）；编排只在这里做一次。</summary>
    private async Task<CaptureResult> CaptureSelectionAsync(nint hwnd, bool clipboardFallback,
        CaptureResult? ocrEvidence = null)
    {
        CloseOcrRange();
        _captureCancellation?.Cancel();
        using var capture = new CancellationTokenSource();
        _captureCancellation = capture;
        try
        {
            var result = ocrEvidence ?? await _captureService.CaptureAsync(new SelectionCaptureRequest(hwnd, RequireNearCursor: true,
                ClipboardFallback: clipboardFallback), capture.Token);
            Trace($"capture result status={result.Status} selected={result.HasSelection} precise={result.Snapshot?.HasPreciseGeometry == true} ocrEnabled={_settings.OcrEnabled}");
            if (_settings.OcrEnabled && ocrEvidence is not null &&
                OcrCapturePolicy.CanFallback(result, explicitRequest: false))
            {
                _popper?.SetStatus(result.WarningCode == "clipboard.restore_failed"
                    ? "剪贴板恢复未完成；正在本地识别…" : "正在本地识别文字块…");
                _ocrInProgress = true;
                var recognized = await _ocrCapture.CaptureAsync(hwnd, result, false,
                    _settings.OcrComponentPath ?? OcrHostClient.DefaultDirectory, capture.Token);
                capture.Token.ThrowIfCancellationRequested();
                Trace($"ocr result status={recognized.Status}");
                if (result.WarningCode == "clipboard.restore_failed") ShowToast("剪贴板恢复未完成，请检查当前内容");
                if (recognized is { Status: CaptureStatus.Success, Snapshot: { } ocrSnapshot })
                {
                    CloseOcrRange();
                    _ocrRange = new OcrRangeWindow(ocrSnapshot);
                    _ocrRange.Closed += (_, _) => _ocrRange = null;
                    _ocrRange.ShowRange();
                }
                return recognized with { WarningCode = result.WarningCode ?? recognized.WarningCode };
            }
            if (result.WarningCode == "clipboard.restore_failed") ShowToast("剪贴板恢复未完成，请检查当前内容");
            return result;
        }
        catch (OperationCanceledException) { return new CaptureResult(CaptureStatus.Canceled); }
        finally
        {
            if (ReferenceEquals(_captureCancellation, capture))
            {
                if (_ocrInProgress && capture.IsCancellationRequested) _popper?.SetStatus("已取消屏幕识别");
                _captureCancellation = null;
                _ocrInProgress = false;
            }
        }
    }

    private void OnCaptureFailed(CaptureResult result, bool dragged = false)
    {
        var status = result.Status;
        if (status == CaptureStatus.Canceled) return;
        if (result.CanSuggestManualOcr(dragged))
        {
            var now = Environment.TickCount64;
            if (_lastManualOcrHintAtMs is not { } last || now - last >= 8000)
            {
                _lastManualOcrHintAtMs = now;
                ShowToast("未能读取选区，可尝试框选翻译");
            }
            return;
        }
        var hint = status switch
            {
                CaptureStatus.Forbidden => "受保护或权限受限的窗口，不读取内容",
                CaptureStatus.Timeout => "目标窗口取词超时，未启动 OCR；持续超时请重启简译",
                CaptureStatus.Busy => "取词服务正在等待目标窗口响应；持续无响应请重启简译",
                CaptureStatus.ClipboardUnavailable => "剪贴板无法安全备份或恢复，已停止自动取词",
                CaptureStatus.OcrUnavailable => "本地 OCR 未就绪，请在设置中检查组件",
                CaptureStatus.OcrNoText => "选区内未识别到文字，请重新选择",
                CaptureStatus.OcrOccluded => "识别区域被其他窗口遮挡，请移开后重试",
                CaptureStatus.OcrFailed => "屏幕 OCR 未完成，请重新选择文字",
                CaptureStatus.Canceled => "取词已取消：选区、焦点或目标窗口发生变化",
                CaptureStatus.Unsupported when _settings.ClipboardFallback =>
                    "未取到可定位的选区，请重新选中文字",
                CaptureStatus.Unsupported => "未读取到选区；右键可启用剪贴板取词",
                _ => "未检测到可读取的选区，请先选中文字"
            };
        if (result.WarningCode == "clipboard.restore_failed") hint = "剪贴板恢复未完成，请检查当前内容后再重试";
        _popper?.SetStatus(hint);
    }

    private void HandleMouseMove(int x, int y)
    {
        if (_hoverSnapshot is null)
        {
            return;
        }

        if (!IsLookupTargetCurrent(_hoverSnapshot))
        {
            ResetHover(hideOverlay: true);
            return;
        }

        if (_hoverSnapshot.IsManualOcr) return;

        var inside = _hoverSnapshot.ContainsPoint(x, y, 8);
        if (inside)
        {
            _pointerInsideSelection = true;
            _hideTimer?.Stop();
            if (_overlay?.IsVisible == true)
            {
                return;
            }

            if (Fingerprint(_hoverSnapshot) == _suppressedFingerprint)
            {
                return;
            }

            if (_dwellTimer?.IsEnabled == true)
            {
                if (Math.Abs(x - _dwellOriginX) > 8 || Math.Abs(y - _dwellOriginY) > 8)
                {
                    StartDwell(x, y);
                }
            }
            else
            {
                StartDwell(x, y);
            }

            return;
        }

        _pointerInsideSelection = false;
        _dwellTimer?.Stop();
        _suppressedFingerprint = null;
        if (_ocrInProgress)
        {
            _captureGeneration++;
            _captureCancellation?.Cancel();
        }

        if (_overlay?.IsVisible == true &&
            _overlay.CurrentSurface == SurfaceMode.Tooltip &&
            !_overlay.ContainsScreenPoint(x, y))
        {
            StartHideTimer();
        }
    }

    private void StartDwell(int x, int y)
    {
        if (_dwellTimer is null)
        {
            return;
        }

        _dwellOriginX = x;
        _dwellOriginY = y;
        _dwellTimer.Stop();
        _dwellTimer.Start();
    }

    private void OnDwellTick(object? sender, EventArgs e)
    {
        _dwellTimer?.Stop();
        if (_ocrInProgress || !_pointerInsideSelection || _hoverSnapshot is null || _overlay?.IsVisible == true)
        {
            Trace($"dwell tick skipped inside={_pointerInsideSelection} snapshot={_hoverSnapshot is not null} overlay={_overlay?.IsVisible}");
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor) ||
            !_hoverSnapshot.ContainsPoint(cursor.X, cursor.Y, 8))
        {
            Trace($"dwell tick cursor left selection {cursor.X},{cursor.Y}");
            return;
        }

        Trace("dwell tick -> lookup");
        _ = ShowHoverResultAsync();
    }

    private async Task ShowHoverResultAsync()
    {
        var snapshot = _hoverSnapshot;
        if (snapshot is null || _dictionary is null)
        {
            return;
        }

        if (_pendingOcr is { } evidence)
        {
            var captureGeneration = ++_captureGeneration;
            var result = await CaptureSelectionAsync(snapshot.ForegroundHwnd, false, evidence);
            if (captureGeneration != _captureGeneration || !_settings.Enabled) return;
            if (!_pointerInsideSelection) return;
            if (result.Status != CaptureStatus.Success || result.Snapshot is null) { OnCaptureFailed(result); return; }
            _pendingOcr = null;
            snapshot = result.Snapshot;
            _hoverSnapshot = snapshot;
        }
        var generation = ++_lookupGeneration;
        await LookupAndShowAsync(snapshot, generation);
    }

    private async Task LookupAndShowAsync(SelectionSnapshot snapshot, int generation)
    {
        if (_dictionary is null || generation != _lookupGeneration)
        {
            return;
        }

        _lookupCancellation?.Cancel();
        using var query = new CancellationTokenSource();
        _lookupCancellation = query;
        var cancellationToken = query.Token;
        try
        {
            Trace($"lookup len={snapshot.Text.Length} gloss={ShouldGloss(snapshot)} kind={TextClassifier.Classify(snapshot.Text)} literal={_settings.LiteralMode} surface={_settings.SurfaceMode}");
            if (ShouldGloss(snapshot) && _glosser is not null)
            {
                var literal = await _glosser.GlossAsync(snapshot.Text, _settings.TargetLang, cancellationToken);
                if (generation != _lookupGeneration ||
                    !IsLookupTargetCurrent(snapshot))
                {
                    return;
                }

                _overlay?.ShowLiteral(snapshot, literal, _settings);
                _popper?.SetStatus(
                    $"生硬直译 · {literal.WordCount} 词 · 未收录 {literal.UnknownCount} · 本地词典");
                if (literal.UnknownCount > 0)
                {
                    ShowToast("存在未收录内容，已用下划线标注");
                }

                return;
            }

            KnowledgeMatches? knowledge = null;
            if (!_settings.LiteralMode && _settings.TranslationMode != SentenceTranslationMode.LocalMt)
            {
                knowledge = await MatchKnowledgeAsync(snapshot.OcrTranslationText ?? snapshot.Text, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _lookupGeneration || !IsLookupTargetCurrent(snapshot)) return;
                if (knowledge.Hits.Count > 0)
                {
                    await TryShowSentenceTranslationAsync(snapshot, generation, cancellationToken, knowledge);
                    return;
                }
            }

            if (!_settings.LiteralMode && (_settings.TargetLang != "zh-Hans" || _settings.SourceLang is not ("en" or "auto")))
            {
                if (await TryShowSentenceTranslationAsync(snapshot, generation, cancellationToken, knowledge)) return;
            }
            var kind = TextClassifier.Classify(snapshot.Text);
            if (!_settings.LiteralMode && kind is TextKind.Sentence or TextKind.Paragraph)
            {
                if (await TryShowSentenceTranslationAsync(snapshot, generation, cancellationToken))
                {
                    return;
                }

                var notReady = DictionaryResult.NotFound(snapshot.Text, LocalMtNotReadyMessage());
                _overlay?.ShowLookup(snapshot, notReady, _settings, "本地 MT · OPUS-MT", openModelSettings: true);
                SetLookupStatus(notReady);
                return;
            }

            var result = await _dictionary.LookupAsync(snapshot.Text, _settings.TargetLang, cancellationToken);
            if (generation != _lookupGeneration ||
                !IsLookupTargetCurrent(snapshot))
            {
                return;
            }

            // 短语词典没命中时可按句子处理。
            if (TextClassifier.ShouldFallbackToSentence(kind, snapshot.Text, result.Found, _settings.LiteralMode))
            {
                if (_settings.LiteralMode && _glosser is not null)
                {
                    var literal = await _glosser.GlossAsync(snapshot.Text, _settings.TargetLang, cancellationToken);
                    if (generation != _lookupGeneration ||
                        !IsLookupTargetCurrent(snapshot))
                    {
                        return;
                    }

                    _overlay?.ShowLiteral(snapshot, literal, _settings);
                    _popper?.SetStatus(
                        $"短语未收录 · 已按逐词直译 · 未收录 {literal.UnknownCount}");
                    if (literal.UnknownCount > 0)
                    {
                        ShowToast("存在未收录内容，已用下划线标注");
                    }

                    return;
                }

                if (await TryShowSentenceTranslationAsync(snapshot, generation, cancellationToken))
                {
                    return;
                }

                var notReady = DictionaryResult.NotFound(snapshot.Text, LocalMtNotReadyMessage());
                _overlay?.ShowLookup(snapshot, notReady, _settings, "本地 MT · OPUS-MT", openModelSettings: true);
                SetLookupStatus(notReady);
                return;
            }

            _overlay?.ShowLookup(snapshot, result, _settings);
            SetLookupStatus(result);
            Trace($"lookup done senses={result.Senses.Count} source={result.Source} overlayVisible={_overlay?.IsVisible}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (generation != _lookupGeneration || !IsLookupTargetCurrent(snapshot))
            {
                return;
            }

            var error = DictionaryResult.NotFound(snapshot.Text, "查询失败，请重新选择后重试");
            _overlay?.ShowLookup(snapshot, error, _settings, "查询");
            SetLookupStatus(error);
        }
        finally
        {
            if (ReferenceEquals(_lookupCancellation, query))
            {
                if (!query.IsCancellationRequested && generation == _lookupGeneration && snapshot.IsManualOcr)
                    _overlay?.StartManualLifetime(snapshot, _settings);
                _lookupCancellation = null;
            }
        }
    }

    /// <summary>
    /// 本地 MT 的完整降级链：输入校验 → 就绪检查 → 翻译 → 输出校验 → 展示。
    /// 返回 true 表示已经处理（包括校验失败/过期），false 表示 MT 未就绪、调用方应提示。
    /// </summary>
    private async Task<bool> TryShowSentenceTranslationAsync(SelectionSnapshot snapshot, int generation,
        CancellationToken cancellationToken, KnowledgeMatches? knowledge = null)
    {
        var mode = _settings.TranslationMode;
        if (mode == SentenceTranslationMode.LocalMt)
            return await TryShowLocalMtAsync(snapshot, generation, cancellationToken);

        var local = mode == SentenceTranslationMode.LlmLocal;
        var label = local ? "LLM · 本机模型" : "LLM · API";
        var options = local ? _settings.LlmLocal : _settings.LlmApi;
        var modelLabel = string.IsNullOrWhiteSpace(options.Model) ? label : $"{label} · {options.Model}";
        bool IsCurrent() => !cancellationToken.IsCancellationRequested && _settings.Enabled &&
            generation == _lookupGeneration && IsLookupTargetCurrent(snapshot);

        try
        {
            if (!local && _settings.OfflineMode)
                throw new LlmTranslationException("离线模式下请在「模型配置」中选择本机模型。");
            if (!IsCurrent()) return true;

            if (local && !string.IsNullOrEmpty(_settings.ManagedLlmStorageRoot))
                options = options with { Address = await ManagedOllama.Shared.EnsureAsync(_settings.ManagedLlmStorageRoot, _settings.OllamaRuntimePath, cancellationToken) };
            var endpoint = LlmTranslationClient.ResolveEndpoint(mode, options.Address);
            var key = local ? null : LlmCredentialStore.Read(endpoint);
            var provider = new LlmTranslationClient(local ? _llmLocalHttp : _llmApiHttp);
            _overlay?.ShowTranslation(snapshot, "正在翻译…", _settings, modelLabel, label, local);
            knowledge ??= await MatchKnowledgeAsync(snapshot.OcrTranslationText ?? snapshot.Text, cancellationToken);
            if (!IsCurrent()) return true;
            var sourceLanguage=_settings.SourceLang;var targetLanguage=_settings.TargetLang;
            var preferenceStore=new TranslationPreferenceRepository(AppPaths.SettingsDatabase);
            var preferenceState=await preferenceStore.LoadAsync(cancellationToken);
            var styles=TextClassifier.Classify(snapshot.OcrTranslationText??snapshot.Text) is TextKind.Sentence or TextKind.Paragraph
                ? sourceLanguage=="auto"?TranslationPreferences.Applicable(preferenceState.Document,targetLanguage):TranslationPreferences.Selected(preferenceState.Document,sourceLanguage,targetLanguage) : [];
            var document = await provider.TranslateDocumentAsync(mode, options, key,
                snapshot.OcrTranslationText ?? snapshot.Text, sourceLanguage, targetLanguage, cancellationToken, knowledge.Hits,styles);
            sourceLanguage=document.SourceLang;
            styles=TranslationPreferences.Selected(preferenceState.Document,sourceLanguage,targetLanguage)
                .Where(rule=>styles.Any(sent=>sent.Id==rule.Id)).ToArray();
            var translated=document.Text;
            if (IsCurrent())
            {
                _overlay?.ShowTranslation(snapshot, translated, _settings, modelLabel+" · "+TranslationLanguages.Label(sourceLanguage)+" → "+TranslationLanguages.Label(targetLanguage), label, local);
                _overlay?.ShowKnowledge(knowledge);
                _overlay?.ShowPreferences(styles,sourceLanguage,targetLanguage);
                var requestKnowledge=knowledge;
                _overlay?.ConfigureAdjustment(document,knowledge,async (current,choice,save,token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if(generation!=_lookupGeneration || !IsLookupTargetCurrent(snapshot))throw new OperationCanceledException();
                    var updated=await provider.AdjustAsync(current,choice,mode,options,key,requestKnowledge.Hits,token,styles);
                    token.ThrowIfCancellationRequested();
                    if(generation!=_lookupGeneration || !IsLookupTargetCurrent(snapshot))throw new OperationCanceledException();
                    if(save)await _moreSettingsRepository!.SavePreferredAsync(requestKnowledge.PresetId,sourceLanguage,targetLanguage,choice,token);
                    return updated;
                },async (original,before,after,token)=>
                {
                    if(generation!=_lookupGeneration || !IsLookupTargetCurrent(snapshot))throw new OperationCanceledException();
                    if(TextClassifier.Classify(original) is not (TextKind.Sentence or TextKind.Paragraph))return;
                    try { await preferenceStore.RecordEditAsync(sourceLanguage,targetLanguage,original,before,after,token); }
                    catch(OperationCanceledException){throw;}
                    catch {ShowToast("译文已确认，偏好候选暂未记录。");}
                });
                _popper?.SetStatus(modelLabel);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                var message = ex is LlmTranslationException ? ex.Message : "模型暂不可用，请检查「模型配置」设置。";
                var error = DictionaryResult.NotFound(snapshot.Text, message);
                _overlay?.ShowLookup(snapshot, error, _settings, modelLabel, openModelSettings: true);
                SetLookupStatus(error);
            }
        }
        return true;
    }

    private async Task<bool> TryShowLocalMtAsync(SelectionSnapshot snapshot, int generation, CancellationToken cancellationToken)
    {
        var textForTranslation = snapshot.OcrTranslationText ?? snapshot.Text;
        var validation = LocalMtGuard.ValidateInput(textForTranslation, _settings.SourceLang, _settings.TargetLang);
        if (!validation.Allowed)
        {
            var blocked = DictionaryResult.NotFound(snapshot.Text, validation.Message!);
            _overlay?.ShowLookup(snapshot, blocked, _settings, "本地 MT");
            SetLookupStatus(blocked);
            return true;
        }

        if (!_settings.LocalMtEnabled || _localMt is not { IsConfigured: true })
        {
            return false;
        }

        try
        {
            bool IsCurrent() => !cancellationToken.IsCancellationRequested && _settings.Enabled &&
                generation == _lookupGeneration && IsLookupTargetCurrent(snapshot);
            var finished = false;
            _overlay?.ShowTranslation(snapshot, "正在本地翻译…", _settings);
            var progress = new Progress<TranslationDocument>(document =>
            {
                if (finished || !IsCurrent()) return;
                _overlay?.ShowTranslation(snapshot, document.Text, _settings,
                    $"本地翻译 · {TranslationLanguages.Label(_settings.SourceLang)} → {TranslationLanguages.Label(_settings.TargetLang)} · 已处理 {document.Units.Count} 段 · 未译 {document.FailedCount}");
            });
            var result = await _localMt.TranslateDocumentAsync(textForTranslation, progress, cancellationToken);
            finished = true;
            if (!IsCurrent()) return true;
            _overlay?.ShowTranslation(snapshot, result.Text, _settings,
                result.FailedCount > 0 ? $"本地翻译 · {result.FailedCount} 段未译，已保留原文" : $"本地翻译 · {TranslationLanguages.Label(_settings.SourceLang)} → {TranslationLanguages.Label(_settings.TargetLang)}",
                openModelSettings: result.FailedCount > 0);
            _popper?.SetStatus(result.FailedCount > 0
                ? $"部分完成 · {result.FailedCount} 段未译，已保留原文" : "本地翻译 · 未联网");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return true; }
        catch (Exception)
        {
            if (generation != _lookupGeneration || !IsLookupTargetCurrent(snapshot))
            {
                return true;
            }

            var error = DictionaryResult.NotFound(snapshot.Text, "本地 MT 暂不可用，请检查模型或重新选择后重试");
            _overlay?.ShowLookup(snapshot, error, _settings, "本地 MT · OPUS-MT", openModelSettings: true);
            SetLookupStatus(error);
            return true;
        }
    }

    private string LocalMtNotReadyMessage() => _settings.LocalMtEnabled
        ? "本地 MT 已启用，但 Host、DLL 或模型文件不完整；请在「模型配置」中检查"
        : "普通句子翻译需要本地 MT；请在托盘菜单的「模型配置」中启用";

    /// <summary>
    /// Rewrites model paths that still point at the pre-portable
    /// %LOCALAPPDATA%\Thanslator location, so the settings DB matches the folder
    /// the app now actually uses.
    /// </summary>
    private async Task MigrateLegacyModelPathAsync()
    {
        string? saved = _settings.LocalMtModelPath;
        if (string.IsNullOrWhiteSpace(saved) || System.IO.Directory.Exists(saved))
        {
            return;
        }

        string fallback = LocalMtOptions.DefaultModelDirectory();
        if (!System.IO.Directory.Exists(fallback))
        {
            return;
        }

        _settings.LocalMtModelPath = fallback;
        if (_repository is not null)
        {
            await _repository.SaveAsync(_settings);
        }

        StartupLog.Write("startup: model path migrated to portable data folder");
    }

    private LocalMtProvider? CreateLocalMtProvider()
    {
        // A saved path can point at the old %LOCALAPPDATA% install (or a USB stick
        // that is gone). Fall back to the portable default instead of reporting a
        // permanently broken model.
        string? savedModelPath = _settings.LocalMtModelPath;
        string modelPath = !string.IsNullOrWhiteSpace(savedModelPath) && System.IO.Directory.Exists(savedModelPath)
            ? savedModelPath
            : LocalMtOptions.DefaultModelDirectory();

        if(!_settings.LocalMtEnabled)return null;
        try
        {
            return new LocalMtProvider(LanguagePack.CreateOptions(
                LocalMtOptions.DefaultHostPath(), modelPath, _settings.SourceLang, _settings.TargetLang,
                $"Thanslator.LocalMt.{Environment.ProcessId}", _settings.LocalMtThreads));
        }
        catch(Exception ex) when(ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        { return null; }
    }

    private async Task SaveLocalMtSettingsAsync(AppSettings settings, string? newApiKey)
    {
        await _settingsWrites.WaitAsync();
        try { await SaveAndApplyModelSettingsAsync(settings, newApiKey); }
        finally { _settingsWrites.Release(); }
    }

    private async Task SaveAndApplyModelSettingsAsync(AppSettings settings, string? newApiKey)
    {
        var repository = _repository ?? throw new InvalidOperationException("Settings storage is unavailable.");
        Uri? credentialEndpoint = null;
        string? oldKey = null;
        if (newApiKey is not null)
        {
            credentialEndpoint = LlmTranslationClient.ResolveEndpoint(SentenceTranslationMode.LlmApi, settings.LlmApi.Address);
            oldKey = LlmCredentialStore.Read(credentialEndpoint);
            LlmCredentialStore.Write(credentialEndpoint, newApiKey);
        }

        try
        {
            await repository.SaveAsync(settings);
        }
        catch
        {
            if (credentialEndpoint is not null)
            {
                if (oldKey is null) LlmCredentialStore.Delete(credentialEndpoint);
                else LlmCredentialStore.Write(credentialEndpoint, oldKey);
            }
            throw;
        }

        await ApplyCommittedModelSettingsAsync(settings);
    }

    private async Task ApplyCommittedModelSettingsAsync(AppSettings settings)
    {
        ResetHover(hideOverlay: true);
        var reload = _localMt is null || _settings.LocalMtModelPath != settings.LocalMtModelPath ||
            _settings.SourceLang != settings.SourceLang || _settings.TargetLang != settings.TargetLang;
        _settings.TranslationMode = settings.TranslationMode;
        _settings.SourceLang = settings.SourceLang;
        _settings.TargetLang = settings.TargetLang;
        _settings.LlmApi = settings.LlmApi;
        _settings.LlmLocal = settings.LlmLocal;
        _settings.LocalMtModelPath = settings.LocalMtModelPath;
        _settings.ModelDownloadDirectory = settings.ModelDownloadDirectory;
        _settings.OllamaRuntimePath = settings.OllamaRuntimePath;
        _settings.ManagedLlmStorageRoot = settings.ManagedLlmStorageRoot;
        _settings.LocalMtEnabled = settings.LocalMtEnabled;
        if (_settings.OcrComponentPath != settings.OcrComponentPath)
        {
            CancelManualOcr();
            _ocrCapture.Stop();
            _settings.OcrComponentPath = settings.OcrComponentPath;
        }

        if (reload)
        {
            var previous = _localMt;
            _localMt = CreateLocalMtProvider();
            if (previous is not null) await previous.DisposeAsync();
        }
        try { await RefreshTranslationContextAsync(); }
        catch (Exception)
        {
            Trace("translation context: unable to reload combinations after saving");
            _popper?.UpdateTranslationButtons();
        }
        _popper?.SetStatus(settings.TranslationMode switch
        {
            SentenceTranslationMode.LlmApi => "句段翻译 · LLM API",
            SentenceTranslationMode.LlmLocal => "句段翻译 · 本机 LLM",
            _ => settings.LocalMtEnabled && _localMt is { IsConfigured: true }
                ? "本地 MT 已启用" : LocalMtNotReadyMessage()
        });
    }

    private void OpenLocalMtSettings() => OpenMoreSettings("models/translation");

    private bool ShouldGloss(SelectionSnapshot snapshot)
    {
        if (!_settings.LiteralMode)
        {
            return false;
        }

        return TextClassifier.Classify(snapshot.Text) switch
        {
            TextKind.Word => false,
            TextKind.Phrase => _settings.IncludePhrases,
            _ => true
        };
    }

    private void StartHideTimer()
    {
        if (_hideTimer is null)
        {
            return;
        }

        if (!_hideTimer.IsEnabled) _hideTimer.Start();
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        if (_pointerInsideSelection || _overlay is null || !_overlay.IsVisible)
        {
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor) &&
            _overlay.CurrentSurface == SurfaceMode.Tooltip &&
            !_overlay.ContainsScreenPoint(cursor.X, cursor.Y))
        {
            _overlay.HideOverlay();
            CancelLookup();
        }
    }

    private void OnOverlayDismissed(object? sender, EventArgs e)
    {
        CloseOcrRange();
        if (_hoverSnapshot?.IsManualOcr == true)
        {
            ResetHover(hideOverlay: false);
            return;
        }
        if (_hoverSnapshot is not null)
        {
            _suppressedFingerprint = Fingerprint(_hoverSnapshot);
        }

        CancelLookup();
    }

    private void CancelLookup()
    {
        _lookupGeneration++;
        _lookupCancellation?.Cancel();
    }

    private void ResetHover(bool hideOverlay)
    {
        CloseOcrRange();
        _pendingOcr = null;
        _ocrEditingSnapshot = null;
        _captureGeneration++;
        _captureCancellation?.Cancel();
        CancelLookup();
        _dwellTimer?.Stop();
        _hideTimer?.Stop();
        _hoverSnapshot = null;
        _pointerInsideSelection = false;
        if (hideOverlay)
        {
            _overlay?.HideOverlay();
        }
    }

    private bool IsOverOwnUi(int x, int y) =>
        (_popper?.ContainsScreenPoint(x, y) ?? false) ||
        (_overlay?.ContainsScreenPoint(x, y) ?? false);

    private bool IsOwnWindow(nint hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }

    private bool IsLookupTargetCurrent(SelectionSnapshot snapshot)
    {
        if (snapshot.IsManualOcr) return ReferenceEquals(snapshot, _hoverSnapshot);
        var foreground = NativeMethods.GetForegroundWindow();
        return foreground == snapshot.ForegroundHwnd ||
            (ReferenceEquals(snapshot, _ocrEditingSnapshot) && _overlay is { IsVisible: true } &&
             foreground == new WindowInteropHelper(_overlay).Handle);
    }

    private void CloseOcrRange()
    {
        var range = _ocrRange;
        _ocrRange = null;
        range?.Close();
    }

    private static string Fingerprint(SelectionSnapshot snapshot) =>
        $"{snapshot.ForegroundHwnd}:{snapshot.Text.GetHashCode(StringComparison.Ordinal)}:" +
        $"{snapshot.Bounds.Left:0},{snapshot.Bounds.Top:0},{snapshot.Bounds.Width:0},{snapshot.Bounds.Height:0}";

    private void SetLookupStatus(DictionaryResult result)
    {
        if (_popper is null)
        {
            return;
        }

        _popper.SetStatus(result.Found
            ? $"{result.Headword} · {result.Senses.Count} 组释义 · 本地词典"
            : result.Message ?? "未找到释义");
    }

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

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, "Thanslator.SingleInstance", out var isFirst);
            if (isFirst)
            {
                StartShowPopperListener();
                return true;
            }

            try
            {
                using var signal = EventWaitHandle.OpenExisting("Thanslator.ShowPopper");
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 老版本实例没有监听器，静默退出即可
            }
            catch (UnauthorizedAccessException)
            {
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch (Exception ex)
        {
            StartupLog.Write($"startup: single-instance check failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 第二个实例运行时会 Set 这个事件，这里唤起已有实例的控制条——
    /// 系统托盘图标被折叠进「隐藏的图标」或任务栏自动隐藏时，用户仍有办法找回控制条。
    /// </summary>
    private void StartShowPopperListener()
    {
        _showPopperSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Thanslator.ShowPopper");
        _showPopperThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showPopperSignal.WaitOne())
                    {
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Dispatcher.BeginInvoke(new Action(ShowPopper));
            }
        })
        {
            IsBackground = true,
            Name = "Thanslator.ShowPopper"
        };
        _showPopperThread.Start();
    }

    /// <summary>托盘用打包进程序的 app.ico；失败时退回系统默认图标，不让启动失败。</summary>
    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Drawing.Icon(stream, 32, 32);
            }
        }
        catch (Exception ex)
        {
            StartupLog.Write($"tray: icon load failed {ex.GetType().Name}: {ex.Message}");
        }

        return Drawing.SystemIcons.Application;
    }

    private bool CreateTray()
    {
        try
        {
            _trayIcon = LoadTrayIcon();
            _tray = new TrayIcon("简译 · 选中文本后悬停", _trayIcon.Handle, ShowPopper, ShowTrayMenu, StartupLog.Write);
            _tray.Add();
            StartupLog.Write($"tray: added={_tray.IsAdded}");
            return _tray.IsAdded;
        }
        catch (Exception ex)
        {
            StartupLog.Write($"tray: creation failed {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void ShowTrayMenu()
    {
        _popper?.ShowContextMenuAtMouse();
    }

    private void ShowToast(string message)
    {
        _toast?.Close();
        var toast = new ToastWindow(message, TimeSpan.FromSeconds(2.2));
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_toast, toast))
            {
                _toast = null;
            }
        };
        _toast = toast;
        toast.Show();
    }

    private void ShowPopper()
    {
        if (_popper is null)
        {
            return;
        }

        _popper.ShowFromTray();
        _popper.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _llmApiHttp.Dispose();
        _llmLocalHttp.Dispose();
        CancelManualOcr();
        _ocrCapture.Dispose();
        ResetHover(hideOverlay: true);
        _invalidationMonitor?.Dispose();
        _captureService.Dispose();
        StopMouseHook();
        try
        {
            _localMt?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Best-effort host shutdown.
        }

        if (_tray is not null)
        {
            _tray.Dispose();
        }

        _trayIcon?.Dispose();

        try
        {
            _showPopperSignal?.Dispose();
        }
        catch (Exception)
        {
            // 事件句柄释放失败不影响退出
        }

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        _toast?.Close();
        _overlay?.Close();
        _popper?.Close();
        _moreSettingsWindow?.Close();
        ManagedOllama.Shared.Dispose();
        base.OnExit(e);
    }
}


