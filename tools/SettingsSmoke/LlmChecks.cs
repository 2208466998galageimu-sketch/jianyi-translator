using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Thanslator.Core;
using Thanslator.Translation;

internal static class LlmChecks
{
    public static async Task RunAsync(string address,string output)
    {
        using var http=new System.Net.Http.HttpClient(new RecordingHandler(output));
        var client=new LlmTranslationClient(http);var options=new LlmConnectionSettings(address,"jianyi-qa");
        var first=new KnowledgeHit("domain","领域库",new("a","sensor","传感器"));
        var other=new KnowledgeHit("personal","个人库",new("b","sensor","感应器"));
        var watch=Stopwatch.StartNew();
        var document=await client.TranslateDocumentAsync(SentenceTranslationMode.LlmLocal,options,null,
            "The sensor is ready. Save the file before closing the app.","en","zh-Hans",default,[first]);
        var firstMs=watch.ElapsedMilliseconds;
        if(document.Units.Count!=2 || document.FailedCount!=0)throw new Exception("Actual model did not return two aligned units.");
        watch.Restart();
        var adjusted=await client.AdjustAsync(document,other,SentenceTranslationMode.LlmLocal,options,null,[first],default);
        if(adjusted.Units[1]!=document.Units[1] || !adjusted.Units[0].Translation!.Contains("感应器"))throw new Exception("Actual model adjustment changed unrelated text or ignored terminology.");
        var adjustmentMs=watch.ElapsedMilliseconds;
        var rules=await client.ExtractPreferencesAsync(SentenceTranslationMode.LlmLocal,options,null,
            "原文：1. Restart the app.\n认可译文：1. 重新启动应用。\n原文：2. Save the file.\n认可译文：2. 保存文件。",
            "en","zh-Hans","软件文档",default);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{document,adjusted,rules,firstMs,adjustmentMs},new JsonSerializerOptions{WriteIndented=true}));
    }
    private sealed class RecordingHandler(string output):System.Net.Http.DelegatingHandler(new System.Net.Http.HttpClientHandler{UseProxy=false,AllowAutoRedirect=false})
    {
        private int _number;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token)
        {
            var response=await base.SendAsync(request,token);
            await File.WriteAllTextAsync(output+".response-"+(++_number)+".json",await response.Content.ReadAsStringAsync(token),token);
            return response;
        }
    }
}
