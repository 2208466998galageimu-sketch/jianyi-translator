using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Thanslator.Core;

namespace Thanslator.Capture;

/// <summary>有限可见区域截图 → 本地 OCR → 指向行块；不得从无选区悬停进入。</summary>
public sealed class ScreenOcrCapture : IDisposable
{
    private OcrHostClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CaptureResult> CaptureAsync(nint hwnd, CaptureResult evidence, bool explicitRequest,
        string componentDirectory, CancellationToken cancellationToken)
    {
        if (!OcrCapturePolicy.CanFallback(evidence, explicitRequest)) return evidence;
        return await CaptureCoreAsync(hwnd, evidence, null, componentDirectory, cancellationToken);
    }

    public Task<CaptureResult> CaptureRegionAsync(nint hwnd, RectD region, string componentDirectory,
        CancellationToken cancellationToken) => CaptureCoreAsync(hwnd, null, region, componentDirectory, cancellationToken);

    public static bool IsValidRegion(RectD region, RectD area) =>
        region.Width is >= 8 and <= 3840 && region.Height is >= 8 and <= 2160 &&
        double.IsFinite(region.Left) && double.IsFinite(region.Top) &&
        region.Left == Math.Truncate(region.Left) && region.Top == Math.Truncate(region.Top) &&
        region.Width == Math.Truncate(region.Width) && region.Height == Math.Truncate(region.Height) &&
        area.Contains(region.Left, region.Top) && area.Contains(region.Right, region.Bottom);

    public static Size GetRecognitionSize(int width, int height)
    {
        var scale = Math.Min(1, Math.Min(1600.0 / width,
            Math.Min(1000.0 / height, Math.Sqrt(1500000.0 / ((double)width * height)))));
        return new Size(Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
    }

    public static RectD? GetRegionBounds(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var client) || !ClientToScreen(hwnd, ref client.Origin)) return null;
        var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        var left = Math.Max(client.Left, work.Left);
        var top = Math.Max(client.Top, work.Top);
        var right = Math.Min(client.Left + client.Right, work.Right);
        var bottom = Math.Min(client.Top + client.Bottom, work.Bottom);
        return right > left && bottom > top ? new RectD(left, top, right - left, bottom - top) : null;
    }

    private async Task<CaptureResult> CaptureCoreAsync(nint hwnd, CaptureResult? evidence, RectD? manualRegion,
        string componentDirectory, CancellationToken cancellationToken)
    {
        if (!OcrHostClient.IsConfigured(componentDirectory)) return new(CaptureStatus.OcrUnavailable);
        if (!await _gate.WaitAsync(0, cancellationToken)) return new(CaptureStatus.Busy);
        byte[]? png = null;
        try
        {
            if (_client is null || !string.Equals(_client.DirectoryPath, componentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _client?.Dispose();
                _client = new OcrHostClient(componentDirectory);
            }
            var allowed = CaptureSecurity.CheckWindow(hwnd);
            if (allowed != CaptureStatus.Success) return new(allowed);
            if (!NativeMethods.GetCursorPos(out var point) || !GetClientRect(hwnd, out var client) ||
                !ClientToScreen(hwnd, ref client.Origin)) return new(CaptureStatus.OcrFailed);
            var work = System.Windows.Forms.Screen.FromPoint(new Point(point.X, point.Y)).WorkingArea;
            var selection = evidence is { HasSelection: true, Snapshot.HasPreciseGeometry: true }
                ? evidence.Snapshot.LineRects : null;
            var planned = PlanRegion(new(client.Left, client.Top, client.Right, client.Bottom),
                new(work.Left, work.Top, work.Width, work.Height), point.X, point.Y, selection);
            var region = new RectD(Math.Floor(planned.Left), Math.Floor(planned.Top), Math.Ceiling(planned.Width), Math.Ceiling(planned.Height));
            if (selection is not null)
            {
                selection = selection.Where(rect => rect.Right > region.Left && rect.Left < region.Right &&
                    rect.Bottom > region.Top && rect.Top < region.Bottom).Take(257)
                    .Select(rect => new RectD(Math.Max(rect.Left, region.Left), Math.Max(rect.Top, region.Top),
                        Math.Min(rect.Right, region.Right)-Math.Max(rect.Left, region.Left),
                        Math.Min(rect.Bottom, region.Bottom)-Math.Max(rect.Top, region.Top))).ToArray();
                if (selection.Count == 0) return new(CaptureStatus.OcrNoText);
                if (selection.Count > 256) return new(CaptureStatus.OcrFailed);
            }
            if (manualRegion is { } chosen)
            {
                if (GetRegionBounds(hwnd) is not { } area || !IsValidRegion(chosen, area))
                    return new(CaptureStatus.OcrFailed);
                region = chosen;
                selection = [chosen];
            }
            if (region.Width <= 0 || region.Height <= 0 || (selection is null &&
                (!region.Contains(point.X, point.Y, 8) || NativeMethods.RootWindow(WindowFromPoint(point)) != hwnd)))
                return new(CaptureStatus.OcrNoText);
            if (IsOccluded(hwnd, region, out var ownWindows) || (manualRegion is not null && ownWindows.Count > 0))
                return new(CaptureStatus.OcrOccluded);
            var recognitionSize = GetRecognitionSize((int)region.Width, (int)region.Height);
            var raster = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var bitmap = new Bitmap((int)region.Width, (int)region.Height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen((int)region.Left, (int)region.Top, 0, 0, bitmap.Size);
                    foreach (var rect in ownWindows)
                        graphics.FillRectangle(Brushes.White, (float)(rect.Left-region.Left), (float)(rect.Top-region.Top),
                            (float)rect.Width, (float)rect.Height);
                }
                // 可信选区只保留行内像素，不把包围盒空隙送入识别器。
                if (selection is not null)
                {
                    using var graphics = Graphics.FromImage(bitmap);
                    using var outside = new Region(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    foreach (var rect in selection)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        outside.Exclude(new RectangleF((float)(rect.Left-region.Left), (float)(rect.Top-region.Top), (float)rect.Width, (float)rect.Height));
                    }
                    graphics.FillRegion(Brushes.White, outside);
                }
                using var stream = new MemoryStream();
                if (recognitionSize.Width == bitmap.Width && recognitionSize.Height == bitmap.Height)
                    bitmap.Save(stream, ImageFormat.Png);
                else
                {
                    using var resized = new Bitmap(recognitionSize.Width, recognitionSize.Height);
                    using (var graphics = Graphics.FromImage(resized))
                    {
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(bitmap, 0, 0, resized.Width, resized.Height);
                    }
                    resized.Save(stream, ImageFormat.Png);
                }
                return stream.ToArray();
            }, cancellationToken);
            png = raster;
            cancellationToken.ThrowIfCancellationRequested();
            allowed = CaptureSecurity.CheckWindow(hwnd);
            if (allowed != CaptureStatus.Success) return new(allowed);
            if (IsOccluded(hwnd, region, out var afterWindows) || !ownWindows.SequenceEqual(afterWindows))
                return new(CaptureStatus.OcrOccluded);
            var lines = await _client.RecognizeAsync(png, recognitionSize.Width, recognitionSize.Height, cancellationToken);
            var screenLines = lines.Select(line => line with { Bounds = line.Bounds with
                { Left = line.Bounds.Left * region.Width / recognitionSize.Width + region.Left,
                    Top = line.Bounds.Top * region.Height / recognitionSize.Height + region.Top,
                    Width = line.Bounds.Width * region.Width / recognitionSize.Width,
                    Height = line.Bounds.Height * region.Height / recognitionSize.Height } }).ToArray();
            var block = selection is not null ? OcrBlockSelector.InSelection(screenLines, selection)
                : OcrBlockSelector.AtPoint(screenLines, point.X, point.Y);
            if (block is null || block.Lines.All(line => string.IsNullOrWhiteSpace(line.Text))) return new(CaptureStatus.OcrNoText);
            allowed = CaptureSecurity.CheckWindow(hwnd);
            if (allowed != CaptureStatus.Success) return new(allowed);
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return new(CaptureStatus.Success, new SelectionSnapshot(block.Text, block.Lines.Select(line => line.Bounds).ToArray(),
                hwnd, process.ProcessName, SelectionCaptureMethod.Ocr, Environment.TickCount64,
                RecognitionConfidence: block.Confidence, IsManualOcr: manualRegion is not null,
                OcrTranslationText: block.GetTranslationText()), true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(CaptureStatus.Canceled); }
        catch (OperationCanceledException) { return new(CaptureStatus.Timeout); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or
            System.Runtime.InteropServices.ExternalException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return new(CaptureStatus.OcrFailed); }
        finally { if (png is not null) Array.Clear(png); _gate.Release(); }
    }

    public static RectD PlanRegion(RectD client, RectD work, double x, double y, IReadOnlyList<RectD>? selection)
    {
        var left = Math.Max(client.Left, work.Left); var top = Math.Max(client.Top, work.Top);
        var right = Math.Min(client.Right, work.Right); var bottom = Math.Min(client.Bottom, work.Bottom);
        var desired = new RectD(x-600, y-250, 1200, 700);
        if (selection is { Count: > 0 })
        {
            var bounds = selection.Aggregate((a, b) => a.Union(b));
            desired = new RectD(bounds.Left-8, bounds.Top-8, bounds.Width+16, bounds.Height+16);
        }
        var width = Math.Min(1200, Math.Max(0, Math.Min(right-left, desired.Width)));
        var height = Math.Min(700, Math.Max(0, Math.Min(bottom-top, desired.Height)));
        return new RectD(Math.Clamp(desired.Left, left, Math.Max(left, right-width)),
            Math.Clamp(desired.Top, top, Math.Max(top, bottom-height)), width, height);
    }

    private static bool IsOccluded(nint target, RectD region, out IReadOnlyList<RectD> ownWindows)
    {
        var foundTarget = false;
        var blocked = false;
        var masks = new List<RectD>();
        EnumWindows((window, _) =>
        {
            if (window == target) { foundTarget = true; return false; }
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(window, out var rect)) { blocked = true; return false; }
            if (rect.Right > region.Left && rect.Left < region.Right && rect.Bottom > region.Top && rect.Top < region.Bottom)
            {
                NativeMethods.GetWindowThreadProcessId(window, out var pid);
                if (pid == Environment.ProcessId)
                {
                    masks.Add(new(rect.Left, rect.Top, rect.Right-rect.Left, rect.Bottom-rect.Top));
                    return true;
                }
                blocked = true; return false;
            }
            return true;
        }, 0);
        ownWindows = masks;
        return blocked || !foundTarget;
    }

    public void Stop() => _client?.Stop();
    public void Dispose() => _client?.Dispose();
    private delegate bool EnumWindow(nint hwnd, nint state);
    [StructLayout(LayoutKind.Explicit)] private struct NativeRect
    {
        [FieldOffset(0)] public NativeMethods.POINT Origin;
        [FieldOffset(0)] public int Left; [FieldOffset(4)] public int Top;
        [FieldOffset(8)] public int Right; [FieldOffset(12)] public int Bottom;
    }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativeMethods.POINT point);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
