using System.IO;
using System.Text.Json;

namespace Thanslator.Capture;

public sealed record OcrProfile(int FormatVersion, string Engine, string Detector, string Recognizer, string Characters)
{
    public static OcrProfile? Read(string root)
    {
        var manifest = Path.Combine(root, "ocr.json");
        if (!File.Exists(manifest)) return null;
        if (new FileInfo(manifest).Length > 8192) throw new InvalidDataException("OCR 配置过大。");
        var profile = JsonSerializer.Deserialize<OcrProfile>(File.ReadAllText(manifest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("OCR 配置无效。");
        if (profile.FormatVersion != 1 || profile.Engine != "paddle-ctc" ||
            string.IsNullOrWhiteSpace(profile.Detector) || string.IsNullOrWhiteSpace(profile.Recognizer) || string.IsNullOrWhiteSpace(profile.Characters) ||
            profile.Detector == profile.Recognizer || !profile.Detector.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ||
            !profile.Recognizer.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || !profile.Characters.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不支持的 OCR 模型配置。");
        foreach (var file in new[] {profile.Detector, profile.Recognizer, profile.Characters})
        {
            var path = Resolve(root, file);
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 1024L * 1024 * 1024)
                throw new InvalidDataException("OCR 模型或字符表缺失。");
        }
        return profile;
    }

    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("OCR 文件路径无效。");
        return Path.Combine(root, relative);
    }
}
