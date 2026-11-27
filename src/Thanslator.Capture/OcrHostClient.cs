using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Thanslator.Core;

namespace Thanslator.Capture;

/// <summary>只与本实例启动的离线 OCR 子进程通信；取消时结束推理，不保存图片。</summary>
public sealed class OcrHostClient(string directory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private System.Threading.Timer? _idle;
    private long _lastUse;
    private bool _disposed;
    public string DirectoryPath { get; } = directory;
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "OcrHost");

    public static bool IsConfigured(string directory)
    {
        try
        {
            if (OcrProfile.Read(directory) is not null) return File.Exists(Path.Combine(DefaultDirectory, "Jianyi.OcrHost.exe"));
            return new[] { "Jianyi.OcrHost.exe", "models/ch_PP-OCRv6_tiny_det_infer.onnx",
                "models/ch_PP-OCRv6_small_rec_infer.onnx", "models/ppocrv6_dict.txt" }.All(file => File.Exists(Path.Combine(directory, file)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { return false; }
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(byte[] png, int width, int height, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not { HasExited: false })
            {
                Stop();
                var profile = OcrProfile.Read(DirectoryPath);
                var runtime = profile is null ? DirectoryPath : DefaultDirectory;
                var start = new ProcessStartInfo(Path.Combine(runtime, "Jianyi.OcrHost.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = DirectoryPath,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
                };
                if (profile is not null) { start.ArgumentList.Add("--model-directory"); start.ArgumentList.Add(DirectoryPath); }
                _process = Process.Start(start) ?? throw new IOException("ocr.unavailable");
                _ = _process.StandardError.ReadToEndAsync();
                var ready = await _process.StandardOutput.ReadLineAsync(timeout.Token);
                using var message = JsonDocument.Parse(ready ?? "{}");
                if (!message.RootElement.TryGetProperty("ready", out var value) || value.ValueKind != JsonValueKind.True)
                    throw new IOException("OCR 组件无法加载此模型，请检查模型、字符表及组件版本。");
                _idle ??= new System.Threading.Timer(_ =>
                {
                    if (Environment.TickCount64 - Interlocked.Read(ref _lastUse) < 300_000 || !_gate.Wait(0)) return;
                    try { Stop(); } finally { _gate.Release(); }
                }, null, 60_000, 60_000);
            }
            var request = JsonSerializer.Serialize(new { image = Convert.ToBase64String(png) });
            await _process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
            await _process.StandardInput.FlushAsync(timeout.Token);
            var response = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            return ParseResponse(response ?? "{}", width, height);
        }
        catch
        {
            Stop();
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _lastUse, Environment.TickCount64);
            _gate.Release();
        }
    }

    /// <summary>子进程边界：限制行数/文字长度，拒绝非有限分数与越界矩形。</summary>
    public static IReadOnlyList<OcrLine> ParseResponse(string json, int width, int height)
    {
        if (json.Length > 1024 * 1024) throw new IOException("ocr.invalid_response");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("lines", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 256)
            throw new IOException("ocr.invalid_response");
        var result = new List<OcrLine>();
        foreach (var row in rows.EnumerateArray())
        {
            var text = row.GetProperty("text").GetString();
            var x = row.GetProperty("x").GetDouble(); var y = row.GetProperty("y").GetDouble();
            var w = row.GetProperty("width").GetDouble(); var h = row.GetProperty("height").GetDouble();
            var confidence = row.GetProperty("confidence").GetDouble();
            if (text is null || text.Length > 4096 ||
                !double.IsFinite(x + y + w + h + confidence) || confidence is < 0 or > 1 ||
                x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > width || y + h > height)
                throw new IOException("ocr.invalid_response");
            result.Add(new OcrLine(text, new RectD(x, y, w, h), string.IsNullOrWhiteSpace(text) ? 0 : confidence));
        }
        return result;
    }

    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _idle?.Dispose();
        Stop();
    }
}
