using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using Thanslator.App;
using Thanslator.Core;
using Thanslator.Storage;

internal static class NativeDataChecks
{
    public static async Task RunAsync(MoreSettingsWindow window,string output,AppSettings settings)
    {
        var database=AppPaths.SettingsDatabase;var backup=new UserDataBackup(database);
        var original=await backup.CreateAsync();var document=(JsonObject)original.DeepClone();document["settings"]!["dwellMs"]=650;
        var models=await new MoreSettingsRepository(database).ModelsAsync();
        void Field(string name,object value)=>typeof(MoreSettingsWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,value);
        Field("_restorePreview", await backup.PreviewAsync(document));
        var restore=typeof(MoreSettingsWindow).GetMethod("RestoreBackupAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task<JsonNode>)restore.Invoke(window,[CancellationToken.None])!;
        if((await new SettingsRepository(database).LoadAsync()).DwellMs!=650 || !Directory.EnumerateFiles(Path.Combine(AppPaths.DataRoot,"backups"),"*.json").Any())
            throw new Exception("Native backup restore/recovery point failed.");
        if((await new MoreSettingsRepository(database).ModelsAsync()).Count!=models.Count)throw new Exception("Restore changed configured models.");
        var root=settings.ModelDownloadDirectory??AppPaths.ModelsDirectory;
        var target=Path.Combine(output,"迁移模型");
        var migration=typeof(MoreSettingsWindow).GetMethod("MigrateModelsToAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task<JsonNode>)migration.Invoke(window,[target,CancellationToken.None])!;
        if(!File.Exists(Path.Combine(root,"llm","fixture-model.txt"))||!File.Exists(Path.Combine(target,"llm","fixture-model.txt")))
            throw new Exception("Migration did not retain original and verified destination files.");
        if((await new SettingsRepository(database).LoadAsync()).ModelDownloadDirectory!=target)throw new Exception("Migration did not commit new download location.");
        var updated=await new MoreSettingsRepository(database).ModelsAsync();
        if(updated.Where(row=>MoreSettingsKnowledge.Text(row,"storageRoot")==target).Count()!=models.Where(row=>MoreSettingsKnowledge.Text(row,"storageRoot")==root).Count())
            throw new Exception("Migration did not update model references.");
        File.WriteAllText(Path.Combine(output,"data-safety-passed.txt"),"Native restore generated recovery point and preserved model connections; model migration verified copies, committed references, retained original files.");
    }
}
