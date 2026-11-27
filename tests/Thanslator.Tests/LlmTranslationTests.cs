using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Thanslator.Core;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class LlmTranslationTests
{
    [Theory]
    [InlineData(SentenceTranslationMode.LlmApi, "https://example.com", "https://example.com/v1/chat/completions")]
    [InlineData(SentenceTranslationMode.LlmApi, "https://example.com/v1/chat/completions/", "https://example.com/v1/chat/completions")]
    [InlineData(SentenceTranslationMode.LlmLocal, "http://localhost:11434/v1", "http://127.0.0.1:11434/v1/chat/completions")]
    [InlineData(SentenceTranslationMode.LlmLocal, "http://[::1]:8080/v1", "http://[::1]:8080/v1/chat/completions")]
    public void Endpoint_UsesCorrectChatPath(SentenceTranslationMode mode, string address, string expected)
    {
        Assert.Equal(expected, LlmTranslationClient.ResolveEndpoint(mode, address).AbsoluteUri);
    }

    [Theory]
    [InlineData(SentenceTranslationMode.LlmApi, "http://example.com/v1")]
    [InlineData(SentenceTranslationMode.LlmApi, "https://localhost/v1")]
    [InlineData(SentenceTranslationMode.LlmLocal, "http://localhost.example.com/v1")]
    [InlineData(SentenceTranslationMode.LlmLocal, "http://192.168.1.2/v1")]
    [InlineData(SentenceTranslationMode.LlmApi, "https://user:pass@example.com/v1")]
    [InlineData(SentenceTranslationMode.LlmApi, "https://example.com/v1?secret=value")]
    public void Endpoint_RejectsUnsafeAddress(SentenceTranslationMode mode, string address)
    {
        Assert.Throws<LlmTranslationException>(() => LlmTranslationClient.ResolveEndpoint(mode, address));
    }

    [Theory]
    [InlineData(SentenceTranslationMode.LlmApi, "https://example.com/v1", true)]
    [InlineData(SentenceTranslationMode.LlmLocal, "http://127.0.0.1:11434/v1", false)]
    public async Task Translate_UsesSelectedModelAndPreservesCode(SentenceTranslationMode mode, string address, bool usesAuth)
    {
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(usesAuth ? "test-key" : null, request.Headers.Authorization?.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("test-model", json.RootElement.GetProperty("model").GetString());
            var userText = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            Assert.Equal("Call ⟦0⟧.", userText);
            return Reply("调用 ⟦0⟧。");
        }));
        var client = new LlmTranslationClient(http);
        var result = await client.TranslateAsync(mode, new(address, "test-model"), "test-key",
            "Call `run()`.", "en", "zh-Hans", CancellationToken.None);
        Assert.Equal("调用 `run()`。", result);
    }

    [Fact]
    public async Task Translate_RejectsDroppedPlaceholder()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply("调用。"))));
        var client = new LlmTranslationClient(http);
        await Assert.ThrowsAsync<LlmTranslationException>(() => client.TranslateAsync(SentenceTranslationMode.LlmLocal,
            new("http://127.0.0.1:11434/v1", "test-model"), null, "Call `run()`.", "en", "zh-Hans", CancellationToken.None));
    }

    [Fact]
    public async Task Translate_ReportsHttpStatusWithoutLeakingPayload()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("service body with private data")
        })));
        var client = new LlmTranslationClient(http);
        var error = await Assert.ThrowsAsync<LlmTranslationException>(() => client.TranslateAsync(SentenceTranslationMode.LlmApi,
            new("https://example.com/v1", "test-model"), "test-key", "Hello.", "en", "zh-Hans", CancellationToken.None));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("private data", error.Message);
    }

    [Fact]
    public async Task Translate_PropagatesUserCancellation()
    {
        using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Reply("ignored");
        }));
        var client = new LlmTranslationClient(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TranslateAsync(SentenceTranslationMode.LlmLocal,
            new("http://127.0.0.1:11434/v1", "test-model"), null, "Hello.", "en", "zh-Hans", cancel.Token));
    }

    [Fact]
    public async Task DomainTranslation_SendsOnlyMatchesAsReferenceAndKeepsProtectedSource()
    {
        var library = new KnowledgeLibrary("sensing", "感知工程", "", "en", "zh-Hans",
            [new("cloud", "point cloud", "点云", "专业术语"), new("unused", "sensor fusion", "传感器融合")]);
        var hits = KnowledgeRules.Match("Read point cloud with `read()`.", "en", "zh-Hans", [library]).Hits;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var messages = json.RootElement.GetProperty("messages");
            using var body = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Assert.Equal("Read point cloud with ⟦0⟧.", body.RootElement.GetProperty("source_text").GetString());
            var terms = body.RootElement.GetProperty("terminology");
            Assert.Equal(1, terms.GetArrayLength());
            Assert.Equal("point cloud", terms[0].GetProperty("term").GetString());
            Assert.Equal("点云", terms[0].GetProperty("translation").GetString());
            Assert.Equal("感知工程", terms[0].GetProperty("library").GetString());
            Assert.Contains("untrusted reference data", messages[0].GetProperty("content").GetString());
            return Reply("用 ⟦0⟧ 读取点云。");
        }));
        var result = await new LlmTranslationClient(http).TranslateAsync(SentenceTranslationMode.LlmLocal,
            new("http://127.0.0.1:11434/v1", "test-model"), null, "Read point cloud with `read()`.",
            "en", "zh-Hans", CancellationToken.None, hits);
        Assert.Equal("用 `read()` 读取点云。", result);
    }

    [Fact]
    public void DomainTranslation_RejectsExcessRulesInsteadOfSilentlyDroppingThem()
    {
        var hit = new KnowledgeHit("id", "库", new("id", "term", "译法"));
        Assert.Throws<LlmTranslationException>(() => LlmTranslationClient.BuildTerminology(Enumerable.Repeat(hit, 101).ToArray()));
        var longHit = hit with { Rule = hit.Rule with { Note = new string('a', 25000) } };
        Assert.Throws<LlmTranslationException>(() => LlmTranslationClient.BuildTerminology([longHit]));
        Assert.Equal("", LlmTranslationClient.BuildTerminology([]));
    }

    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] {
            new { finish_reason = "stop", message = new { content = text } }
        } }), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
