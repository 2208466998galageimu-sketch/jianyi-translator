using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Thanslator.Capture;
using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.App;

public sealed partial class MoreSettingsWindow
{
    private static JsonArray OcrCatalog()
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("Thanslator.App.Settings.ocr-models.json")!;
        return JsonNode.Parse(input)!.AsArray();
    }

    private async Task<JsonNode> DownloadOcrAsync(JsonObject data, CancellationToken token)
    {
        if (!File.Exists(Path.Combine(OcrHostClient.DefaultDirectory,"Jianyi.OcrHost.exe")))
            throw new ArgumentException("请先安装包含 OCR 组件的简译版本。");
        var key = MoreSettingsKnowledge.Text(data,"id");
        var profile = OcrCatalog().OfType<JsonObject>().FirstOrDefault(item => MoreSettingsKnowledge.Text(item,"id") == key)
            ?? throw new ArgumentException("OCR 下载项目无效。");
        var directory = FullDownloadPath(DownloadDirectory());
        var root = Path.Combine(directory, "ocr", key);
        var files = OcrFiles(profile);
        CheckDownloadSpace(directory, ModelFileDownload.Inspect(root, files));
        using var http = ModelFileDownload.CreateClient();
        var progress = DownloadProgress();
        foreach (var file in files) await ModelFileDownload.EnsureAsync(http, root, file, progress, token);
        var license=await File.ReadAllBytesAsync(Path.Combine(AppPaths.AssetsDirectory,"licenses","RapidOCR-LICENSE.txt"),token);
        await File.WriteAllBytesAsync(Path.Combine(root,"LICENSE.txt"),license,token);
        var config = new JsonObject { ["formatVersion"]=1,["engine"]="paddle-ctc",["detector"]="det.onnx",
            ["recognizer"]="rec.onnx",["characters"]="characters.txt",["source"]=profile["source"]!.DeepClone(),["license"]=profile["license"]!.DeepClone() };
        await File.WriteAllTextAsync(Path.Combine(root,"ocr.json.tmp"),config.ToJsonString(),token);
        File.Move(Path.Combine(root,"ocr.json.tmp"),Path.Combine(root,"ocr.json"),true);
        progress.Report("文件已就绪，正在试识别…");
        try { await CheckOcrAsync(root, token); }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        { throw new InvalidDataException("OCR 模型已下载，试识别未通过。可重试检查。", ex); }
        var id = "ocr-" + key;
        var record = new JsonObject { ["id"] = id, ["kind"] = "ocr", ["name"] = profile["name"]!.DeepClone(), ["path"] = root };
        var draft = _settings.Clone();
        draft.OcrComponentPath = root;
        return await CommitDownloadAsync(id, record, draft, token);
    }

    private static async Task CheckOcrAsync(string directory,CancellationToken token)
    {
        if (!OcrHostClient.IsConfigured(directory)) throw new InvalidDataException("OCR 模型或运行组件不完整。");
        using var image = new System.Drawing.Bitmap(320,80);
        using (var graphics = System.Drawing.Graphics.FromImage(image))
        using (var font = new System.Drawing.Font("Segoe UI",24))
        {
            graphics.Clear(System.Drawing.Color.White);
            graphics.DrawString("Test 123",font,System.Drawing.Brushes.Black,12,12);
        }
        using var buffer = new MemoryStream();
        image.Save(buffer,System.Drawing.Imaging.ImageFormat.Png);
        using var worker = new OcrHostClient(directory);
        var lines = await worker.RecognizeAsync(buffer.ToArray(),320,80,token);
        if (!lines.Any(line => line.Text.Contains("Test",StringComparison.OrdinalIgnoreCase) && line.Text.Contains("123")))
            throw new InvalidDataException("OCR 试识别失败，未切换当前模型。");
    }
}

