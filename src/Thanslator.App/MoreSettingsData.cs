using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Thanslator.Core;
using Thanslator.Storage;
using Thanslator.Translation;

namespace Thanslator.App;

public sealed partial class MoreSettingsWindow
{
    private BackupPreview? _restorePreview;
    private bool _closeApproved;

    private JsonObject BasicState() => new() { ["ocrEnabled"]=_settings.OcrEnabled,["clipboardFallback"]=_settings.ClipboardFallback,
        ["offlineMode"]=_settings.OfflineMode,["dwellMs"]=_settings.DwellMs,["surfaceMode"]=_settings.SurfaceMode==SurfaceMode.Panel?"panel":"tooltip" };
    private async Task<JsonNode> SaveBasicAsync(JsonObject data,CancellationToken token)
    {
        var values=data["values"]!.AsObject();var old=BasicState();
        void Apply(JsonObject source)
        {
            if(source["ocrEnabled"] is not null)_settings.OcrEnabled=source["ocrEnabled"]!.GetValue<bool>();
            if(source["clipboardFallback"] is not null)_settings.ClipboardFallback=source["clipboardFallback"]!.GetValue<bool>();
            if(source["offlineMode"] is not null)_settings.OfflineMode=source["offlineMode"]!.GetValue<bool>();
            if(source["dwellMs"] is not null){var dwell=source["dwellMs"]!.GetValue<int>();if(dwell is <200 or >1000)throw new ArgumentException("等待时间须为 200–1000 毫秒。");_settings.DwellMs=dwell;}
            if(source["surfaceMode"] is not null)_settings.SurfaceMode=MoreSettingsKnowledge.Text(source,"surfaceMode") switch {"panel"=>SurfaceMode.Panel,"tooltip"=>SurfaceMode.Tooltip,_=>throw new ArgumentException("呈现方式无效。")};
        }
        try{Apply(values);token.ThrowIfCancellationRequested();await _saveSettings(_settings);}
        catch{Apply(old);throw;}
        return BasicState();
    }

    private async Task<JsonNode?> ExportLibraryAsync(JsonObject data,CancellationToken token)
    {
        var state=await _store.LoadAsync([],token);
        var library=state.Document["libraries"]!.AsArray().FirstOrDefault(row=>MoreSettingsKnowledge.Text(row,"id")==MoreSettingsKnowledge.Text(data,"id")&&!MoreSettingsKnowledge.Deleted(row!))
            ??throw new ArgumentException("知识库已不存在。");
        var name=string.Concat(MoreSettingsKnowledge.Text(library,"name").Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
        var picker=new Microsoft.Win32.SaveFileDialog {Title="导出知识库",Filter="CSV 词表 (*.csv)|*.csv|TSV 词表 (*.tsv)|*.tsv",FileName=name+".csv"};
        if(picker.ShowDialog(this)!=true)return null;
        var separator=picker.FileName.EndsWith(".tsv",StringComparison.OrdinalIgnoreCase)?'\t':',';
        await AtomicExport.WriteTextAsync(picker.FileName,MoreSettingsKnowledge.ExportDelimited(library,separator),new UTF8Encoding(true),token);return JsonValue.Create("已导出词表。");
    }

    private async Task<JsonNode?> ExportBackupAsync(CancellationToken token)
    {
        var picker=new Microsoft.Win32.SaveFileDialog {Title="导出用户数据",Filter="简译备份 (*.json)|*.json",FileName="简译备份-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"};
        if(picker.ShowDialog(this)!=true)return null;
        var skipped = new List<string>();
        var backup = await new UserDataBackup(AppPaths.SettingsDatabase).CreateAsync(token, skipped);
        await AtomicExport.WriteTextAsync(picker.FileName, backup.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false), token);
        return JsonValue.Create(skipped.Count == 0 ? "已导出备份。" : $"已导出备份，跳过 {skipped.Count} 项无效设置。");
    }
    private async Task<JsonNode?> PreviewBackupAsync(CancellationToken token)
    {
        var picker=new Microsoft.Win32.OpenFileDialog {Title="选择简译备份",Filter="简译备份 (*.json)|*.json"};
        if(picker.ShowDialog(this)!=true)return null;
        _restorePreview = null;
        if(new FileInfo(picker.FileName).Length>64*1024*1024)throw new ArgumentException("备份超过 64 MiB，无法导入。");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(picker.FileName, token)) as JsonObject
            ?? throw new ArgumentException("备份内容必须是 JSON 对象。");
        _restorePreview = await new UserDataBackup(AppPaths.SettingsDatabase).PreviewAsync(document, token);
        return new JsonObject { ["name"]=Path.GetFileName(picker.FileName),["libraries"]=document["knowledge"]!["libraries"]!.AsArray().Count,
            ["presets"]=document["knowledge"]!["presets"]!.AsArray().Count,["preferences"]=document["preferences"]!["rules"]!.AsArray().Count };
    }
    private async Task<JsonNode> RestoreBackupAsync(CancellationToken token)
    {
        var preview = _restorePreview ?? throw new ArgumentException("请先选择备份并预览。");
        await _commitDataChange(async () =>
        {
            var store = new UserDataBackup(AppPaths.SettingsDatabase);
            var recovery = await store.CreateAsync(token);
            var folder = Path.Combine(AppPaths.DataRoot, "backups");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            await AtomicExport.WriteTextAsync(path, recovery.ToJsonString(), new UTF8Encoding(false), token);
            return await store.RestoreAsync(preview, token);
        });
        _restorePreview = null;
        return JsonValue.Create("已恢复用户数据。");
    }

    private static readonly string[] ManagedFolders=["language-packs","ocr","imports","llm","runtimes","downloads","temporary","runtime-data"];
    private async Task<JsonNode> StorageStateAsync(CancellationToken token)
    {
        var root=DownloadDirectory();var models=await _store.ModelsAsync(token);
        return await Task.Run<JsonNode>(()=>{
            var groups=new JsonArray();long total=0;
            var extras=models.Select(row=>MoreSettingsKnowledge.Text(row,"path")).Where(path=>path.Length>0&&ModelStorage.Within(path,root))
                .Select(path=>Path.GetRelativePath(root,path).Split(Path.DirectorySeparatorChar)[0]);
            foreach(var folder in ManagedFolders.Concat(extras).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path=ModelStorage.ManagedDirectory(root,Path.Combine(root,folder));var bytes=ModelStorage.Files(path,token).Sum(file=>file.Length);total+=bytes;
                if(bytes>0)groups.Add(new JsonObject{["name"]=folder switch {"language-packs"=>"离线语言包","ocr"=>"OCR 模型","imports"=>"导入模型","llm"=>"本机 LLM","runtimes"=>"运行组件",_=>"其他组件 · "+folder},["bytes"]=bytes});
            }
            return new JsonObject { ["groups"]=groups,["totalBytes"]=total,["freeBytes"]=new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace };
        },token);
    }
    private async Task<JsonNode> CleanDownloadsAsync(CancellationToken token)
    {
        var root=DownloadDirectory();var count=0;
        await Task.Run(()=>{
            foreach(var folder in ManagedFolders)
            foreach(var file in ModelStorage.Files(ModelStorage.ManagedDirectory(root,Path.Combine(root,folder)),token))
                if(file.Name.EndsWith(".download",StringComparison.OrdinalIgnoreCase)){token.ThrowIfCancellationRequested();file.Delete();count++;}
        },token);return JsonValue.Create("已清理 "+count+" 个未完成下载。");
    }
    private async Task<JsonNode?> MigrateModelsAsync(CancellationToken token)
    {
        var picker=new Microsoft.Win32.OpenFolderDialog {Title="选择新的模型位置",InitialDirectory=DownloadDirectory()};if(picker.ShowDialog(this)!=true)return null;
        return await MigrateModelsToAsync(picker.FolderName,token);
    }
    private async Task<JsonNode> MigrateModelsToAsync(string destination,CancellationToken token)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DownloadDirectory()));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var preview = await _store.PreviewModelMigrationAsync(token);
        var models = preview.Models.ToDictionary(row => row.Key, row => JsonNode.Parse(row.Value)!.AsObject());
        var draft = _settings.Clone();
        var modelPaths = models.Values.Select(row => MoreSettingsKnowledge.Text(row, "path"))
            .Concat(new[] { draft.LocalMtModelPath, draft.OcrComponentPath,
                string.IsNullOrEmpty(draft.OllamaRuntimePath) ? null : Path.GetDirectoryName(draft.OllamaRuntimePath) })
            .Where(path => !string.IsNullOrEmpty(path)).Select(path => path!).ToArray();
        if (modelPaths.Any(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(root, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("模型或运行组件直接位于下载根目录，请先放入独立子目录后迁移。");

        await _changed();
        ManagedOllama.Shared.Dispose();
        var nestedOllama = models.Values.Select(row => MoreSettingsKnowledge.Text(row, "storageRoot"))
            .Append(draft.ManagedLlmStorageRoot).Where(path => !string.IsNullOrEmpty(path) && ModelStorage.Within(path, root));
        var directories = ManagedFolders.Select(folder => Path.Combine(root, folder))
            .Concat(modelPaths.Where(path => ModelStorage.Within(path, root)))
            .Concat(nestedOllama.SelectMany(path => new[] { "llm", "runtime-data", "temporary" }.Select(folder => Path.Combine(path!, folder))));
        await ModelStorage.CopyVerifiedAsync(root, target, directories, DownloadProgress(), token);
        string? Remap(string? path) => !string.IsNullOrEmpty(path) && (ModelStorage.Within(path, root) ||
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(root, StringComparison.OrdinalIgnoreCase))
            ? Path.GetFullPath(Path.Combine(target, Path.GetRelativePath(root, path))) : path;
        var replacements = new Dictionary<string, JsonObject>();
        foreach (var (id, record) in models)
        {
            foreach (var key in new[] { "path", "storageRoot" })
            {
                var oldPath = MoreSettingsKnowledge.Text(record, key);
                var newPath = Remap(oldPath);
                if (newPath == oldPath) continue;
                record[key] = newPath;
                replacements[id] = record;
            }
        }
        draft.ModelDownloadDirectory = target;
        draft.LocalMtModelPath = Remap(draft.LocalMtModelPath);
        draft.OcrComponentPath = Remap(draft.OcrComponentPath);
        draft.OllamaRuntimePath = Remap(draft.OllamaRuntimePath);
        draft.ManagedLlmStorageRoot = Remap(draft.ManagedLlmStorageRoot);
        await _commitDataChange(() => _store.CommitModelMigrationAsync(preview, replacements, draft, token));
        return await ModelStateAsync(CancellationToken.None);
    }
}
