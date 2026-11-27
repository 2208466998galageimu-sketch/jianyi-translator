using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class LlmModelManagementTests
{
    [Fact]
    public async Task ModelListUsesSameOriginModelsEndpointAndKeepsManualFallbackOnFailure()
    {
        using var http=new HttpClient(new Handler(request=>{
            Assert.Equal("http://127.0.0.1:1234/v1/models",request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get,request.Method);
            return new(HttpStatusCode.OK){Content=new StringContent("{\"data\":[{\"id\":\"qwen\"},{\"id\":\"qwen\"},{\"id\":\"\"}]}")};
        }));
        var list=await new LlmTranslationClient(http).ListModelsAsync(SentenceTranslationMode.LlmLocal,new("http://127.0.0.1:1234/v1",""),null,default);
        Assert.Equal(["qwen"],list);
        using var unsupported=new HttpClient(new Handler(_=>new(HttpStatusCode.NotFound)));
        var error=await Assert.ThrowsAsync<LlmTranslationException>(()=>new LlmTranslationClient(unsupported).ListModelsAsync(SentenceTranslationMode.LlmLocal,new("http://127.0.0.1:1234/v1",""),null,default));
        Assert.Contains("手动",error.Message);
    }

    [Fact]
    public async Task AssistantRequiresActualJsonResponseAndRejectsPlainConnectionSuccess()
    {
        using var http=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent(new JsonObject {
            ["choices"]=new JsonArray(new JsonObject{["message"]=new JsonObject{["content"]="连接成功"}})}.ToJsonString(),Encoding.UTF8,"application/json")}));
        await Assert.ThrowsAsync<LlmTranslationException>(()=>new LlmTranslationClient(http).TestStructuredAsync(SentenceTranslationMode.LlmLocal,new("http://127.0.0.1:1234/v1","fixture"),null,default));
        Assert.Throws<LlmTranslationException>(()=>LlmTranslationClient.ResolveEndpoint(SentenceTranslationMode.LlmApi,"https://platform.deepseek.com"));
    }

    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(reply(request));}
}
