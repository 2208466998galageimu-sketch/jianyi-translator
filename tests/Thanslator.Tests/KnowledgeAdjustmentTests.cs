using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Thanslator.Core;
using Thanslator.Storage;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class KnowledgeAdjustmentTests
{
    private static JsonObject Document()=>MoreSettingsKnowledge.Migrate([
        new("a","领域","","en","zh-Hans",[new("a1","sensor","传感器")]),
        new("b","个人","","en","zh-Hans",[new("b1","sensor","感应器")]),
        new("c","其他","","en","zh-Hans",[new("c1","sensor","传感器")])],["a","b","c"]);

    [Fact]
    public void CandidatesRetainSameTranslationSources_AndDeletedSourceInvalidatesPreferred()
    {
        var doc=Document();var libraries=MoreSettingsKnowledge.SelectedLibraries(doc,"en","zh-Hans");
        var matches=KnowledgeRules.Match("sensor","en","zh-Hans",libraries);
        Assert.Equal(3,KnowledgeChoices.Candidates(matches,"sensor").Count);
        var choice=matches.Candidates!.Single(hit=>hit.LibraryId=="b");
        KnowledgeChoices.SavePreferred(doc,"default","en","zh-Hans",choice);
        Assert.Equal("感应器",KnowledgeChoices.ApplyPreferred(matches,doc["presets"]![0],"en","zh-Hans").Hits[0].Rule.Translation);
        doc["libraries"]![1]!["deletedAt"]="2026-11-25";
        var now=KnowledgeRules.Match("sensor","en","zh-Hans",MoreSettingsKnowledge.SelectedLibraries(doc,"en","zh-Hans"));
        Assert.Equal("传感器",KnowledgeChoices.ApplyPreferred(now,doc["presets"]![0],"en","zh-Hans").Hits[0].Rule.Translation);
        Assert.Throws<ArgumentException>(()=>KnowledgeChoices.SavePreferred(doc,"default","en","zh-Hans",choice));
    }

    [Fact]
    public async Task FrequencyCountsCandidatesBeforeConflict_ButDeduplicatesRequest()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        try
        {
            await new SettingsRepository(path).InitializeAsync();await new KnowledgeRepository(path).InitializeAsync();
            var store=new MoreSettingsRepository(path);var initial=await store.LoadAsync([]);await store.SaveAsync(Document(),initial.Revision);
            await store.MatchAsync("sensor sensor","en","zh-Hans",[],default,"one");
            await store.MatchAsync("sensor sensor","en","zh-Hans",[],default,"one");
            var doc=(await store.LoadAsync([])).Document;await store.FillUsageAsync(doc,"en","zh-Hans");
            Assert.All(doc["libraries"]!.AsArray(),item=>Assert.Equal(1,item!["hits"]!.GetValue<int>()));
            await store.MatchAsync("sensor","en","zh-Hans",[],default,"two");
            await store.FillUsageAsync(doc,"en","zh-Hans");Assert.All(doc["libraries"]!.AsArray(),item=>Assert.Equal(2,item!["hits"]!.GetValue<int>()));
        }
        finally{SqliteConnection.ClearAllPools();File.Delete(path);}
    }

    [Fact]
    public void AlignedTranslationRejectsMissingIds_AndKeepsLineBreaks()
    {
        var parts=new[]{"First sentence.\n","Second sentence."};
        var doc=LlmTranslationClient.ParseAligned("{\"translations\":[{\"id\":0,\"text\":\"第一句。\"},{\"id\":1,\"text\":\"第二句。\"}]}",parts,"en","zh-Hans");
        Assert.Equal("第一句。\n第二句。",doc.Text);
        Assert.Throws<LlmTranslationException>(()=>LlmTranslationClient.ParseAligned("{\"translations\":[{\"id\":1,\"text\":\"错误\"}]}",parts,"en","zh-Hans"));
    }

    [Fact]
    public async Task TermAdjustmentOnlyChangesMatchingUnit_AndRejectsIgnoredChoice()
    {
        var calls=0;
        using var http=new HttpClient(new Handler(_=>{calls++;return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{content="感应器已准备好。"},finish_reason="stop"}}}),Encoding.UTF8,"application/json")};}));
        var client=new LlmTranslationClient(http);var choice=new KnowledgeHit("b","个人",new("b1","sensor","感应器"));
        var document=new TranslationDocument([new("The sensor is ready. ","传感器已就绪。 "),new("Keep this exact output.","必须保持的译文。")]);
        var adjusted=await client.AdjustAsync(document,choice,SentenceTranslationMode.LlmLocal,new("http://127.0.0.1:8000/v1","fixture"),null,[choice],default);
        Assert.Equal("感应器已准备好。 ",adjusted.Units[0].Translation);Assert.Equal(document.Units[1],adjusted.Units[1]);Assert.Equal(1,calls);
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(reply(request));}
}
