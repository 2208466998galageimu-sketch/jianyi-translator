using System.Net;
using System.Text;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class LocalModelDownloadTests
{
    [Fact]
    public async Task Download_RequiresSuccessAndLocalInstalledConfirmation()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            Assert.True(request.RequestUri.IsLoopback);
            return Task.FromResult(Reply(request.Method == HttpMethod.Post ?
                "{\"status\":\"pulling\",\"completed\":5,\"total\":10}\n{\"status\":\"success\"}\n" :
                "{\"models\":[{\"name\":\"qwen2.5:3b\",\"size\":100}]}"));
        }));
        await new LocalModelDownloads(http).DownloadAsync("http://localhost:11434/v1", "qwen2.5:3b", null, CancellationToken.None);
        Assert.Equal(new[] { "/api/pull", "/api/tags" }, requests);
        Assert.Equal(4, LocalModelDownloads.Recommended.Count);
    }
    [Theory]
    [InlineData("{\"status\":\"pulling\"}\n")]
    [InlineData("{\"error\":\"disk full\"}\n")]
    public async Task Download_DoesNotClaimIncompleteDownloadsAreReady(string response)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(response))));
        await Assert.ThrowsAsync<IOException>(() => new LocalModelDownloads(http).DownloadAsync(
            "http://127.0.0.1:11434/v1", "qwen2.5:3b", null, CancellationToken.None));
    }

    [Fact]
    public async Task Download_ExplainsNonPublicRedirectWithoutExposingRawDetails()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(
            "{\"error\":\"max retries exceeded: redirect target not allowed: download.example resolves to non-public 198.18.0.1\"}\n"))));
        var error = await Assert.ThrowsAsync<IOException>(() => new LocalModelDownloads(http).DownloadAsync(
            "http://127.0.0.1:11434/v1", "qwen2.5:1.5b", null, CancellationToken.None));
        Assert.Contains("非公网地址", error.Message);
        Assert.DoesNotContain("download.example", error.Message);
    }

    [Fact]
    public async Task List_ExcludesCloudAndEmptyModels()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(
            "{\"models\":[{\"name\":\"local\",\"size\":100},{\"name\":\"remote\",\"size\":1,\"remote_host\":\"https://example.com\"},{\"name\":\"empty\",\"size\":0}]}"))));
        Assert.Equal(new[] { "local" }, await new LocalModelDownloads(http).ListAsync("http://localhost:11434/v1", CancellationToken.None));
    }
    [Theory]
    [InlineData("https://example.com/v1")]
    [InlineData("http://192.168.1.2:11434")]
    public void Download_RejectsNonLocalService(string address) =>
        Assert.Throws<LlmTranslationException>(() => LocalModelDownloads.ResolveRoot(address));
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
}
