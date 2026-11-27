using System.Net.Http.Headers;
using System.Text.Json;
using Thanslator.Core;

namespace Thanslator.Translation;

public sealed partial class LlmTranslationClient
{
    public async Task TestStructuredAsync(SentenceTranslationMode mode,LlmConnectionSettings options,string? key,CancellationToken token)
    {
        var answer=await SendChatAsync(mode,options,key,"Return only JSON {\"ready\":true}. Do not include any explanation.","Check JSON output capability.",token,json:true);
        try{using var doc=JsonDocument.Parse(answer);if(doc.RootElement.GetProperty("ready").GetBoolean()!=true)throw new JsonException();}
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException)
        {throw new LlmTranslationException("模型未通过 JSON 输出检测，不能用于导入助手。请更换模型后重试。");}
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(SentenceTranslationMode mode,LlmConnectionSettings options,string? key,CancellationToken token)
    {
        var chat=ResolveEndpoint(mode,options.Address);var endpoint=new UriBuilder(chat) {Path=chat.AbsolutePath[..^"/chat/completions".Length]+"/models"}.Uri;
        if(mode==SentenceTranslationMode.LlmApi&&string.IsNullOrWhiteSpace(key))throw new LlmTranslationException("请先填写 API Key。");
        using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);
        if(mode==SentenceTranslationMode.LlmApi)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if(!response.IsSuccessStatusCode)throw new LlmTranslationException($"无法读取模型列表（HTTP {(int)response.StatusCode}），可手动填写模型名称。");
            await response.Content.LoadIntoBufferAsync(1024*1024,deadline.Token);
            using var document=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
            var rows=document.RootElement.GetProperty("data");if(rows.GetArrayLength()>1000)throw new JsonException();
            return rows.EnumerateArray().Select(row=>row.GetProperty("id").GetString()??"").Where(name=>name.Length is >0 and <=160).Distinct().Order().ToArray();
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new LlmTranslationException("读取模型列表超时，可手动填写模型名称。");}
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException)
        {throw new LlmTranslationException("服务未提供兼容的模型列表，可手动填写模型名称。");}
    }
}
