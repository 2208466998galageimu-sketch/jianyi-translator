using System.Net.Http;
using System.Text.Json.Nodes;
using Thanslator.Core;
using Thanslator.Storage;
using Thanslator.Translation;
using Thanslator.App.Interop;

namespace Thanslator.App;

public sealed partial class MoreSettingsWindow
{
    private async Task<JsonObject> PreferencesStateAsync(CancellationToken token)
    {
        var state=await new TranslationPreferenceRepository(AppPaths.SettingsDatabase).LoadAsync(token);
        return new(){["document"]=state.Document,["revision"]=state.Revision};
    }

    private async Task<JsonNode> SavePreferencesAsync(JsonObject data,CancellationToken token)
    {
        var store=new TranslationPreferenceRepository(AppPaths.SettingsDatabase);
        var state=await store.LoadAsync(token);
        if(state.Revision!=data["revision"]!.GetValue<long>())throw new ArgumentException("偏好已有更新，请重新打开页面后修改。");
        var values=data["values"]!.AsObject();
        state.Document["rules"]=values["rules"]!.DeepClone();state.Document["collect"]=values["collect"]!.DeepClone();state.Document["scope"]=values["scope"]!.DeepClone();state.Document["enabled"]=values["enabled"]?.DeepClone()??JsonValue.Create(true);
        await store.SaveAsync(state.Document,state.Revision,token);
        return await PreferencesStateAsync(token);
    }

    private async Task<JsonNode> ConfirmPreferenceAsync(JsonObject data,CancellationToken token)
    {
        var store=new TranslationPreferenceRepository(AppPaths.SettingsDatabase);var state=await store.LoadAsync(token);
        var candidate=state.Document["candidates"]!.AsArray().FirstOrDefault(row=>MoreSettingsKnowledge.Text(row,"id")==MoreSettingsKnowledge.Text(data,"id"))
            ??throw new ArgumentException("候选已不存在。");
        if(candidate["ready"]?.GetValue<bool>()!=true || MoreSettingsKnowledge.Text(candidate,"status")!="pending")throw new ArgumentException("候选尚未满足确认条件。");
        if(data["ignore"]?.GetValue<bool>()==true)candidate["status"]="ignored";
        else
        {
            state.Document["rules"]!.AsArray().Add(new JsonObject { ["id"]=Guid.NewGuid().ToString("N"),["source"]=candidate["source"]!.DeepClone(),
                ["target"]=candidate["target"]!.DeepClone(),["scope"]=candidate["scope"]!.DeepClone(),["rule"]=MoreSettingsKnowledge.Text(data,"rule"),["enabled"]=true });
            candidate["status"]="accepted";
        }
        await store.SaveAsync(state.Document,state.Revision,token);return await PreferencesStateAsync(token);
    }

    private async Task<JsonNode> ExtractPreferencesAsync(JsonObject data,CancellationToken token)
    {
        var mode=_settings.TranslationMode;
        if(mode==SentenceTranslationMode.LocalMt)throw new ArgumentException("请先选择 LLM 翻译模式。");
        var local=mode==SentenceTranslationMode.LlmLocal;
        if(!local && _settings.OfflineMode)throw new ArgumentException("离线模式不能调用远程模型。");
        var source=MoreSettingsKnowledge.Text(data,"source");var target=MoreSettingsKnowledge.Text(data,"target");
        if((source!="auto" && !MoreSettingsKnowledge.ValidLanguage(source)) || !MoreSettingsKnowledge.ValidLanguage(target) || source==target)
            throw new ArgumentException("请选择不同的原文和译文语言。");
        var examples=MoreSettingsKnowledge.Text(data,"examples");var scope=MoreSettingsKnowledge.Text(data,"scope");
        if(examples.Length is <1 or >8000 || scope.Length>80)throw new ArgumentException("示例应为 1–8000 字，场景不超过 80 字。");
        var options=local?_settings.LlmLocal:_settings.LlmApi;
        if(local && !string.IsNullOrEmpty(_settings.ManagedLlmStorageRoot))
            options=options with {Address=await ManagedOllama.Shared.EnsureAsync(_settings.ManagedLlmStorageRoot,_settings.OllamaRuntimePath,token)};
        var endpoint=LlmTranslationClient.ResolveEndpoint(mode,options.Address);
        if(!local && !RemoteRequestPreview.Confirm(this,"从示例提取偏好",endpoint,options.Model,examples))throw new OperationCanceledException();
        using var http=LlmTranslationClient.CreateHttpClient(local);
        var rules=await new LlmTranslationClient(http).ExtractPreferencesAsync(mode,options,local?null:LlmCredentialStore.Read(endpoint),examples,source,target,scope,token);
        return new JsonArray(rules.Select(rule=>(JsonNode)new JsonObject{["rule"]=rule.Rule,["evidence"]=rule.Evidence}).ToArray());
    }
}
