using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Thanslator.App.Interop;
using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.App;

public sealed partial class MoreSettingsWindow
{
    private async Task<JsonNode> ListModelsAsync(JsonObject data,CancellationToken token)
    {
        var options=await ResolveConnectionAsync(data,token);var kind=MoreSettingsKnowledge.Text(data,"kind");
        var mode=kind=="llm"?SentenceTranslationMode.LlmLocal:kind=="api"?SentenceTranslationMode.LlmApi:Mode(options.Address);
        if(_settings.OfflineMode&&mode==SentenceTranslationMode.LlmApi)throw new ArgumentException("离线模式不能连接远程服务。");
        var key=MoreSettingsKnowledge.Text(data,"key");
        if(key.Length==0)key=kind=="assistant"?LlmCredentialStore.Read(SecretEndpoint(options.Address))??"":LlmCredentialStore.Read(LlmTranslationClient.ResolveEndpoint(mode,options.Address))??"";
        using var http=LlmTranslationClient.CreateHttpClient(mode==SentenceTranslationMode.LlmLocal);
        var models=await new LlmTranslationClient(http).ListModelsAsync(mode,options,key,token);
        return new JsonArray(models.Select(name=>(JsonNode)JsonValue.Create(name)!).ToArray());
    }
    private async Task<JsonNode> CheckSavedModelAsync(JsonObject data,CancellationToken token)
    {
        var record=(await _store.ModelsAsync(token)).FirstOrDefault(row=>MoreSettingsKnowledge.Text(row,"id")==MoreSettingsKnowledge.Text(data,"id"))
            ??throw new ArgumentException("配置已不存在。");
        var kind=MoreSettingsKnowledge.Text(record,"kind");
        try
        {
            if(kind=="ocr")await CheckOcrAsync(MoreSettingsKnowledge.Text(record,"path"),token);
            else if(kind=="mt")await CheckDirectionsAsync(MoreSettingsKnowledge.Text(record,"path"),record["keys"]!.AsArray().Select(row=>row!.GetValue<string>()).Take(1).ToArray(),token);
            else await TestModelAsync(record,token);
            record["lastCheck"]="passed";record["lastCheckMessage"]="检测通过";
        }
        catch(Exception error)when(error is LlmTranslationException or ArgumentException or IOException or HttpRequestException)
        {record["lastCheck"]="failed";record["lastCheckMessage"]=error is LlmTranslationException or ArgumentException?error.Message:"模型文件或服务不可用，请检查路径与连接。";}
        record["lastCheckedAt"]=DateTimeOffset.UtcNow.ToString("O");
        await _store.SaveModelAsync(MoreSettingsKnowledge.Text(record,"id"),record,token);return await ModelStateAsync(token);
    }

    private bool CanDeleteFiles(JsonObject record,IReadOnlyList<JsonObject> all)
    {
        try
        {
            var kind=MoreSettingsKnowledge.Text(record,"kind");var id=MoreSettingsKnowledge.Text(record,"id");
            if(kind=="llm")
            {
                var root=MoreSettingsKnowledge.Text(record,"storageRoot");
                return root.Length>0&&Path.GetFullPath(root).Equals(Path.GetFullPath(DownloadDirectory()),StringComparison.OrdinalIgnoreCase)&&
                    !all.Any(other=>MoreSettingsKnowledge.Text(other,"id")!=id&&MoreSettingsKnowledge.Text(other,"storageRoot")==root&&MoreSettingsKnowledge.Text(other,"model")==MoreSettingsKnowledge.Text(record,"model"));
            }
            if(kind is not ("mt" or "ocr"))return false;
            var path=ModelStorage.ManagedDirectory(DownloadDirectory(),MoreSettingsKnowledge.Text(record,"path"));
            return !all.Any(other=>MoreSettingsKnowledge.Text(other,"id")!=id&&MoreSettingsKnowledge.Text(other,"path") is {Length:>0} value&&
                (Path.GetFullPath(value).Equals(path,StringComparison.OrdinalIgnoreCase)||ModelStorage.Within(value,path)||ModelStorage.Within(path,value)));
        }
        catch(Exception error)when(error is ArgumentException or IOException or UnauthorizedAccessException){return false;}
    }
    private async Task DeleteManagedFilesAsync(JsonObject record,CancellationToken token)
    {
        var kind=MoreSettingsKnowledge.Text(record,"kind");
        if(kind is "mt" or "ocr")
        {
            var path=ModelStorage.ManagedDirectory(DownloadDirectory(),MoreSettingsKnowledge.Text(record,"path"));
            _=ModelStorage.Files(path,token);token.ThrowIfCancellationRequested();
            if(Directory.Exists(path))await Task.Run(()=>Directory.Delete(path,true),token);
        }
        else if(kind=="llm")
        {
            var root=MoreSettingsKnowledge.Text(record,"storageRoot");var address=await ManagedOllama.Shared.EnsureAsync(root,_settings.OllamaRuntimePath,token);
            using var http=LlmTranslationClient.CreateHttpClient(true);
            using var request=new HttpRequestMessage(HttpMethod.Delete,new Uri(LocalModelDownloads.ResolveRoot(address),"api/delete")) {
                Content=JsonContent.Create(new {model=MoreSettingsKnowledge.Text(record,"model")}) };
            using var response=await http.SendAsync(request,token);
            if(!response.IsSuccessStatusCode)throw new ArgumentException("本机运行组件未能删除模型文件，配置仍保留。");
            _localDownloads.Remove((FullDownloadPath(root), MoreSettingsKnowledge.Text(record, "model")));
        }
    }
}
