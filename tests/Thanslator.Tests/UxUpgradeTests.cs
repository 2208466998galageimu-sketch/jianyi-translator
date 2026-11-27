using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class UxUpgradeTests
{
    [Fact]
    public void DisabledPreferencesDoNotApply_AndAnySourceRuleAppliesToExplicitLanguage()
    {
        var state=new JsonObject { ["enabled"]=false,["scope"]="",["rules"]=new JsonArray(
            new JsonObject { ["id"]="a",["source"]="auto",["target"]="zh-Hans",["scope"]="",["rule"]="保留段落" }) };
        Assert.Empty(TranslationPreferences.Selected(state,"auto","zh-Hans"));
        state["enabled"]=true;
        Assert.Single(TranslationPreferences.Selected(state,"fr","zh-Hans"));
        Assert.Empty(TranslationPreferences.Selected(state,"fr","en"));
    }

    [Fact]
    public void CjkSentenceWithPunctuationIsNotAWord()
    {
        Assert.Equal(TextKind.Sentence,TextClassifier.Classify("请保留原来的数字编号。"));
        Assert.Equal(TextKind.Word,TextClassifier.Classify("传感器"));
        Assert.Equal(TextKind.Word,TextClassifier.Classify(".NET"));
    }

    [Fact]
    public async Task AutomaticTranslationReturnsDetectedLanguageAndPlainTranslation()
    {
        using var http=new HttpClient(new Reply());
        var result=await new LlmTranslationClient(http).TranslateDocumentAsync(SentenceTranslationMode.LlmLocal,
            new("http://127.0.0.1:11434/v1","fixture"),null,"Bonjour le monde.","auto","zh-Hans",default);
        Assert.Equal("fr",result.SourceLang);
        Assert.Equal("你好，世界。",result.Text);
    }

    private sealed class Reply:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var content="{\"source_language\":\"fr\",\"translations\":[{\"id\":0,\"text\":\"你好，世界。\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(
                new JsonObject { ["choices"]=new JsonArray(new JsonObject { ["message"]=new JsonObject { ["content"]=content } }) }.ToJsonString(),Encoding.UTF8,"application/json") });
        }
    }
}
