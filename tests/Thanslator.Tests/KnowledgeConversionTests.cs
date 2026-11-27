using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class KnowledgeConversionTests
{
    private static string Payload(string term, string translation, string source) => JsonSerializer.Serialize(new {
        terms = new[] { new { term, translation, source } }, unresolved = Array.Empty<string>() });

    [Fact]
    public void Conversion_RequiresVerbatimPairAndPreservesSource()
    {
        var result = KnowledgeConversion.Parse(Payload("point cloud", "点云", "point cloud → 点云"),
            "专业术语\npoint cloud → 点云\n", "terms.md", []);
        Assert.True(result.CanImport);
        var rule = Assert.Single(result.Terms);
        Assert.Equal("point cloud", rule.Term);
        Assert.Equal("点云", rule.Translation);
        Assert.Contains("terms.md", rule.Note);
        Assert.Contains("point cloud → 点云", rule.Note);
    }

    [Theory]
    [InlineData("point cloud", "点云数据", "point cloud → 点云数据")]
    [InlineData("point cloud", "点云", "made up excerpt point cloud → 点云")]
    [InlineData("point cloud", "", "point cloud → 点云")]
    [InlineData("point cloud", "点云", "sensor fusion → 传感器融合")]
    public void Conversion_RejectsInventedTranslationsAndUnverifiablePairings(string term, string translation, string source)
    {
        var preview = KnowledgeConversion.Parse(Payload(term, translation, source), "point cloud → 点云\nsensor fusion → 传感器融合", "terms.txt", []);
        Assert.False(preview.CanImport);
        Assert.Empty(preview.Terms);
        Assert.NotEmpty(preview.Errors);
    }

    [Fact]
    public void Conversion_ReportsUnresolvedAndDuplicateWithoutPartialConfirmation()
    {
        const string source = "point cloud → 点云";
        var existing = new KnowledgeTerm("id", "POINT CLOUD", "点云");
        Assert.False(KnowledgeConversion.Parse(Payload("point cloud", "点云", source), source, "terms.md", [existing]).CanImport);
        var json = "{\"terms\":[],\"unresolved\":[\"sensor fusion 缺少译法\"]}";
        var preview = KnowledgeConversion.Parse(json, "sensor fusion", "terms.md", []);
        Assert.False(preview.CanImport);
        Assert.Contains("待处理", Assert.Single(preview.Errors).Message);
        Assert.False(KnowledgeConversion.Parse("{\"terms\":[]}", "nothing", "terms.md", []).CanImport);
    }

    [Fact]
    public void Documents_ReadTextWordAndExcelWithoutExecutingContent()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"jianyi-doc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var textPath = Path.Combine(folder, "terms.md");
            File.WriteAllText(textPath, "point cloud → 点云", new UTF8Encoding(false));
            Assert.Equal("point cloud → 点云", KnowledgeDocument.Read(textPath));
            var word = Path.Combine(folder, "terms.docx");
            CreateZip(word, ("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>point cloud → 点云</w:t></w:r></w:p></w:body></w:document>"));
            Assert.Contains("point cloud → 点云", KnowledgeDocument.Read(word));
            var excel = Path.Combine(folder, "terms.xlsx");
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            CreateZip(excel, ("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\"><si><t>point cloud</t></si></sst>"),
                ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData><row><c t=\"s\"><v>0</v></c><c t=\"inlineStr\"><is><t>点云</t></is></c></row></sheetData></worksheet>"));
            Assert.Contains("point cloud\t点云", KnowledgeDocument.Read(excel));
            File.WriteAllText(textPath, new string('x', 30001));
            Assert.Throws<ArgumentException>(() => KnowledgeDocument.Read(textPath));
            CreateZip(Path.Combine(folder, "bad.docx"), ("word/document.xml", "<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///does-not-exist'>]><x>&y;</x>"));
            Assert.Throws<System.Xml.XmlException>(() => KnowledgeDocument.Read(Path.Combine(folder, "bad.docx")));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task Client_SendsExtractionRequestAndValidatesReturnedEvidence()
    {
        const string source = "point cloud → 点云";
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var messages = body.RootElement.GetProperty("messages");
            Assert.Contains("NOT translation or knowledge generation", messages[0].GetProperty("content").GetString());
            using var document = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Assert.Equal(source, document.RootElement.GetProperty("document").GetString());
            return Reply(Payload("point cloud", "点云", source));
        }));
        var preview = await new KnowledgeConversionClient(http).ConvertAsync(SentenceTranslationMode.LlmLocal,
            new("http://127.0.0.1:11434/v1", "model"), null, source, "terms.md", "en", "zh-Hans", [], CancellationToken.None);
        Assert.True(preview.CanImport);
    }

    [Fact]
    public async Task Client_RejectsTruncationAndPropagatesCancellation()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply("{}", "length"))));
        await Assert.ThrowsAsync<LlmTranslationException>(() => new KnowledgeConversionClient(http).ConvertAsync(
            SentenceTranslationMode.LlmLocal, new("http://127.0.0.1:11434/v1", "model"), null,
            "point cloud → 点云", "terms.md", "en", "zh-Hans", [], CancellationToken.None));
        using var cancel = new CancellationTokenSource();
        using var canceledHttp = new HttpClient(new Handler(async (_, token) =>
        {
            cancel.Cancel(); await Task.Delay(Timeout.Infinite, token); return Reply("{}");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new KnowledgeConversionClient(canceledHttp).ConvertAsync(
            SentenceTranslationMode.LlmLocal, new("http://127.0.0.1:11434/v1", "model"), null,
            "point cloud → 点云", "terms.md", "en", "zh-Hans", [], cancel.Token));
    }
    private static void CreateZip(string path, params (string Name, string Text)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        { using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
    }
    private static HttpResponseMessage Reply(string content, string reason = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] {
            new { finish_reason = reason, message = new { content } } } }), Encoding.UTF8, "application/json")
    };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
}
