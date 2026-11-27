using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class PromptBoundaryTests
{
    [Fact]
    public void PromptJsonKeepsChineseReadable_WithoutBreakingJson()
    {
        var json=LlmTranslationClient.SerializePrompt(new{translation="感应器",source="sensor"});
        Assert.Contains("感应器",json);using var parsed=JsonDocument.Parse(json);
        Assert.Equal("感应器",parsed.RootElement.GetProperty("translation").GetString());
    }

    [Fact]
    public async Task ExtractedPreferencesRequireAnExactExampleExcerpt()
    {
        using var http=new HttpClient(new Handler());
        var rules=await new LlmTranslationClient(http).ExtractPreferencesAsync(SentenceTranslationMode.LlmLocal,new("http://127.0.0.1:9000/v1","fixture"),null,
            "1. 保存文件。","en","zh-Hans","",default);
        var rule=Assert.Single(rules);Assert.Equal("保留数字编号",rule.Rule);Assert.Equal("1. 保存文件。",rule.Evidence);
    }

    [Fact]
    public async Task ZipCopyRejectsDeclaredOverLimitBeforeCreatingFile()
    {
        var directory=Path.Combine(Path.GetTempPath(),"jianyi-zip-limit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var path=Path.Combine(directory,"model.zip");
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Create)){using var data=zip.CreateEntry("model.bin").Open();data.Write(new byte[128]);}
            using var read=ZipFile.OpenRead(path);var target=Path.Combine(directory,"model.bin");
            await Assert.ThrowsAsync<InvalidDataException>(()=>ModelArchive.CopyEntryAsync(read.Entries[0],target,64,default));Assert.False(File.Exists(target));
        }
        finally{Directory.Delete(directory,true);}
    }
    private sealed class Handler:HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            using var input=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("json_object",input.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            var content=LlmTranslationClient.SerializePrompt(new{rules=new[]{new{rule="保留数字编号",evidence="1. 保存文件。"},new{rule="臆测的规则",evidence="输入没有的内容"}}});
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content}}}}))};
        }
    }
}
