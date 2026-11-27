using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Thanslator.Core;
using Thanslator.Storage;

namespace Thanslator.Tests;

public sealed class TranslationPreferenceTests
{
    [Fact]
    public async Task PassiveCollectionIsOffByDefault_DeduplicatesAndRequiresDifferentDays()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");var clock=new Clock();
        try
        {
            var store=new TranslationPreferenceRepository(path,clock);var initial=await store.LoadAsync();
            Assert.False(initial.Document["collect"]!.GetValue<bool>());
            await store.RecordEditAsync("en","zh-Hans","Use ABC now.","现在使用。","现在使用 ABC。",default);
            Assert.Empty((await store.LoadAsync()).Document["candidates"]!.AsArray());
            initial.Document["collect"]=true;initial.Document["enabled"]=false;await store.SaveAsync(initial.Document,initial.Revision);
            await store.RecordEditAsync("en","zh-Hans","Use ABC now.","现在使用。","现在使用 ABC。",default);
            initial=await store.LoadAsync();Assert.Empty(initial.Document["candidates"]!.AsArray());
            initial.Document["enabled"]=true;await store.SaveAsync(initial.Document,initial.Revision);
            await store.RecordEditAsync("en","zh-Hans","Use ABC now.","现在使用。","现在使用 ABC。",default);
            await store.RecordEditAsync("en","zh-Hans","Use ABC now.","现在使用。","现在使用 ABC。",default);
            await store.RecordEditAsync("en","zh-Hans","Check DEF now.","现在检查。","现在检查 DEF。",default);
            await store.RecordEditAsync("en","zh-Hans","Keep GHI here.","保留。","保留 GHI。",default);
            var state=await store.LoadAsync();var candidate=state.Document["candidates"]![0]!;
            Assert.Equal(3,candidate["samples"]!.AsArray().Count);Assert.False(candidate["ready"]!.GetValue<bool>());
            clock.Today=clock.Today.AddDays(1);
            await store.RecordEditAsync("en","zh-Hans","Read JKL next.","然后读取。","然后读取 JKL。",default);
            state=await store.LoadAsync();Assert.True(state.Document["candidates"]![0]!["ready"]!.GetValue<bool>());
            Assert.Empty(state.Document["rules"]!.AsArray());
            var stored=state.Document.ToJsonString();Assert.DoesNotContain("Use ABC",stored);Assert.DoesNotContain("现在使用",stored);
        }
        finally{SqliteConnection.ClearAllPools();File.Delete(path);}
    }

    [Fact]
    public void ScopeAndLanguagePreventUnrelatedStyleRules()
    {
        var state=new JsonObject{["scope"]="科研",["rules"]=new JsonArray(
            new JsonObject{["id"]="a",["source"]="en",["target"]="zh-Hans",["scope"]="科研",["rule"]="保持编号"},
            new JsonObject{["id"]="b",["source"]="en",["target"]="zh-Hans",["scope"]="商务",["rule"]="保持称呼"})};
        Assert.Single(TranslationPreferences.Selected(state,"en","zh-Hans"));
        Assert.Empty(TranslationPreferences.Selected(state,"zh-Hans","en"));
    }
    private sealed class Clock:TimeProvider
    {public DateTimeOffset Today=new(2026,11,25,0,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Today;}
}
