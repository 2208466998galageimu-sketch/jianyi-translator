using System.IO;
using System.Net.Http;
using Thanslator.Core;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Thanslator.Capture;
using Thanslator.Translation;

namespace Thanslator.App;

public sealed partial class MoreSettingsWindow
{
    private sealed record PendingModelImport(string Kind, string File, string Error = "", bool CanRepair = false);
    private PendingModelImport? _pendingModelImport;

    private async Task<JsonNode?> ImportModelAsync(JsonObject data, CancellationToken token)
    {
        var kind = MoreSettingsKnowledge.Text(data,"kind");
        if (kind is not ("mt" or "llm" or "ocr")) throw new ArgumentException("导入类型无效。");
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "从资源管理器导入",
            Filter = kind == "llm" ? "本地模型 (*.gguf;*.zip)|*.gguf;*.zip" : "模型或组件包 (*.zip)|*.zip" };
        if (picker.ShowDialog(this) != true) return null;
        _pendingModelImport = new(kind,picker.FileName);
        return await ImportPendingModelAsync(false,token);
    }

    private async Task<JsonNode> ImportPendingModelAsync(bool repair, CancellationToken token)
    {
        var pending = _pendingModelImport ?? throw new ArgumentException("没有待处理的导入文件，请重新选择。");
        if (repair && !pending.CanRepair) throw new ArgumentException("此导入问题不能由助手修改，请查看文件要求。");
        var directory = Path.Combine(DownloadDirectory(),"imports",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var staging=directory;var committed=false;
        try
        {
            if (pending.Kind == "llm")
            {
                var file = pending.File.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)
                    ? await ModelArchive.ExtractGgufAsync(pending.File,directory,token) : pending.File;
                var result = await ImportGgufAsync(file,token);
                _pendingModelImport = null;
                return await ModelStateAsync(token);
            }
            if (pending.Kind == "mt")
            {
                if (repair) directory = await RepairModelLayoutAsync(pending.File,directory,LocalMtModelInstaller.RequiredFiles,token);
                else await LocalMtModelInstaller.InstallFromZipAsync(pending.File,directory,null,token);
            }
            else directory = await InstallOcrImportAsync(pending.File,directory,repair,token);
            var id = Guid.NewGuid().ToString("N");
            var record = new JsonObject { ["id"] = id, ["kind"] = pending.Kind,
                ["name"] = Path.GetFileNameWithoutExtension(pending.File), ["path"] = directory };
            if (pending.Kind == "mt")
            {
                var pack = LanguagePack.Read(directory,verifyHashes:true);
                var keys = pack?.Directions.Select(pair=>pair.SourceLanguage+":"+pair.TargetLanguage).ToArray() ?? ["en:zh-Hans"];
                record["keys"] = new JsonArray(keys.Select(key=>(JsonNode)JsonValue.Create(key)!).ToArray());
                if (pack is not null) record["name"] = pack.Name;
                await CheckDirectionsAsync(directory,keys,token);
            }
            await _store.SaveModelAsync(id,record,token);
            try { await ActivateModelAsync(new() { ["id"]=id },token); }
            catch { await _store.DeleteModelAsync(id,CancellationToken.None); throw; }
            _pendingModelImport = null;
            committed=true;
            return await ModelStateAsync(token);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or LlmTranslationException or HttpRequestException or IOException)
        {
            _pendingModelImport = pending with { Error = ex.Message, CanRepair = CanRepairImport(pending) };
            return await ModelStateAsync(token);
        }
        finally
        {
            if(!committed && Directory.Exists(staging))
            {
                try { Directory.Delete(staging,true); }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool CanRepairImport(PendingModelImport pending)
    {
        if (pending.Kind == "llm" || !pending.File.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var zip = ZipFile.OpenRead(pending.File);
            var entries = zip.Entries.Where(entry=>!string.IsNullOrEmpty(entry.Name)).ToArray();
            if (entries.Length > 500 || entries.Sum(entry=>entry.Length)>4L*1024*1024*1024) return false;
            if (pending.Kind == "mt")
            {
                // A declared package with bad hashes or capabilities is never repaired by guessing.
                if (entries.Any(entry=>entry.Name=="manifest.json")) return false;
                return entries.Count(entry=>entry.Name.EndsWith(".spm",StringComparison.OrdinalIgnoreCase))==2 &&
                    entries.Any(entry=>entry.Name.EndsWith(".bin",StringComparison.OrdinalIgnoreCase)) &&
                    entries.Count(entry=>entry.Name.EndsWith(".json",StringComparison.OrdinalIgnoreCase))>=2;
            }
            if(entries.Any(entry=>entry.Name=="ocr.json"))return false;
            return entries.Count(entry=>entry.Name.EndsWith(".onnx",StringComparison.OrdinalIgnoreCase))==2 &&
                entries.Count(entry=>entry.Name.EndsWith(".txt",StringComparison.OrdinalIgnoreCase))==1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException) { return false; }
    }

    private async Task<string> InstallOcrImportAsync(string file,string directory,bool repair,CancellationToken token)
    {
        if (repair)
            directory = await RepairModelLayoutAsync(file,directory,
                ["models/ch_PP-OCRv6_tiny_det_infer.onnx","models/ch_PP-OCRv6_small_rec_infer.onnx","models/ppocrv6_dict.txt"],token);
        else await ModelArchive.ExtractAsync(file,directory,token);
        var locations=new[] {directory}.Concat(Directory.GetDirectories(directory)).ToArray();
        foreach(var location in locations)
            if(!File.Exists(Path.Combine(location,"ocr.json")) && new[]{"det.onnx","rec.onnx","characters.txt"}.All(name=>File.Exists(Path.Combine(location,name))))
                await File.WriteAllTextAsync(Path.Combine(location,"ocr.json"),"{\"formatVersion\":1,\"engine\":\"paddle-ctc\",\"detector\":\"det.onnx\",\"recognizer\":\"rec.onnx\",\"characters\":\"characters.txt\"}",token);
        var roots = locations.Where(OcrHostClient.IsConfigured).ToArray();
        if (roots.Length == 0 && repair)
        {
            var profile = new OcrProfile(1,"paddle-ctc","models/ch_PP-OCRv6_tiny_det_infer.onnx",
                "models/ch_PP-OCRv6_small_rec_infer.onnx","models/ppocrv6_dict.txt");
            await File.WriteAllTextAsync(Path.Combine(directory,"ocr.json"),System.Text.Json.JsonSerializer.Serialize(profile,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }),token);
            _ = OcrProfile.Read(directory);
            roots = [directory];
        }
        if (roots.Length==1) { await CheckOcrAsync(roots[0],token); return roots[0]; }
        throw new InvalidDataException("OCR 包需要完整组件或受支持的模型与字符表。");
    }
}
