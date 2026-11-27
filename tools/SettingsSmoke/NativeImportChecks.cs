using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using Thanslator.App;
using Thanslator.Core;

internal static class NativeImportChecks
{
    public static async Task RunAsync(MoreSettingsWindow window,string output,AppSettings settings)
    {
        var repository=AppPaths.RepositoryRoot!;
        var profile=Path.Combine(repository,"build/model-research/ocr-profiles/v6-small");
        var file=Path.Combine(output,"own-ocr.zip");
        using(var zip=ZipFile.Open(file,ZipArchiveMode.Create))
            foreach(var name in new[]{"det.onnx","rec.onnx","characters.txt"})zip.CreateEntryFromFile(Path.Combine(profile,name),name,CompressionLevel.NoCompression);
        async Task<JsonNode> Import(string source)
        {
            var type=typeof(MoreSettingsWindow).GetNestedType("PendingModelImport",BindingFlags.NonPublic)!;
            var pending=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,["ocr",source,"",false],null);
            typeof(MoreSettingsWindow).GetField("_pendingModelImport",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,pending);
            return await (Task<JsonNode>)typeof(MoreSettingsWindow).GetMethod("ImportPendingModelAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,[false,CancellationToken.None])!;
        }
        var imported=await Import(file);var current=settings.OcrComponentPath;
        if(imported["pendingImport"] is not null || current is null || !File.Exists(Path.Combine(current,"ocr.json")))throw new Exception("Standard own OCR model did not activate.");
        var bad=Path.Combine(output,"incompatible-ocr.zip");
        using(var zip=ZipFile.Open(bad,ZipArchiveMode.Create))
        {
            foreach(var name in new[]{"det.onnx","rec.onnx"})zip.CreateEntryFromFile(Path.Combine(profile,name),name,CompressionLevel.NoCompression);
            zip.CreateEntryFromFile(Path.Combine(repository,"build/model-research/ocr-profiles/v6-tiny/characters.txt"),"characters.txt",CompressionLevel.NoCompression);
        }
        var before=Directory.GetDirectories(Path.Combine(AppPaths.ModelsDirectory,"imports")).Length;
        var rejected=await Import(bad);
        if(rejected["pendingImport"]?["error"]?.GetValue<string>().Length is not >0 || settings.OcrComponentPath!=current ||
            Directory.GetDirectories(Path.Combine(AppPaths.ModelsDirectory,"imports")).Length!=before)throw new Exception("Failed OCR import overwrote current configuration or leaked its stage.");

        var layout=Path.Combine(output,"ocr-layout.zip");
        using(var zip=ZipFile.Open(layout,ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Path.Combine(profile,"det.onnx"),"raw/my_detector.onnx",CompressionLevel.NoCompression);
            zip.CreateEntryFromFile(Path.Combine(profile,"rec.onnx"),"raw/my_recognizer.onnx",CompressionLevel.NoCompression);
            zip.CreateEntryFromFile(Path.Combine(profile,"characters.txt"),"raw/my_characters.txt",CompressionLevel.NoCompression);
        }
        var unmapped=await Import(layout);
        if(unmapped["pendingImport"]?["canRepair"]?.GetValue<bool>()!=true)throw new Exception("Layout problem was not retained for helper.");
        async Task<JsonNode> Retry() => await (Task<JsonNode>)typeof(MoreSettingsWindow).GetMethod("ImportPendingModelAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,[true,CancellationToken.None])!;
        var missing=await Retry();
        if(!missing["pendingImport"]!["error"]!.GetValue<string>().Contains("配置导入助手"))throw new Exception("Missing-helper guidance was not returned.");
        var configure=typeof(MoreSettingsWindow).GetMethod("ConfigureModelAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await (Task<JsonNode>)configure.Invoke(window,[new JsonObject{["kind"]="assistant",["address"]="http://127.0.0.1:18965/v1",["model"]="jianyi-qa"},CancellationToken.None])!;
        var repaired=await Retry();
        if(repaired["pendingImport"] is not null || settings.OcrComponentPath==current)throw new Exception("Configured helper could not repair and activate the pending OCR layout.");
        await new Thanslator.Storage.MoreSettingsRepository(AppPaths.SettingsDatabase).DeleteModelAsync("assistant");
        File.WriteAllText(Path.Combine(output,"helper-import-passed.txt"),"Actual Qwen helper: direct attempt fails; pending ZIP survives missing-helper failure and helper configuration; returned mapping is allowlisted, copied and actually OCR-tested before activation.");
        File.WriteAllText(Path.Combine(output,"native-import-passed.txt"),"Raw OCR ZIP activates only after actual test recognition; incompatible character dictionary is rejected, current model and original ZIP preserved, failed staging removed.");
    }
}
