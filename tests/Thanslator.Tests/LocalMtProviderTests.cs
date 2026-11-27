using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class LocalMtProviderTests
{
    [Fact]
    public async Task BrokenPlaceholder_RetriesOriginalOnceAndRestoresContext()
    {
        await using var fixture = new PipeFixture(text => text.Contains("__JY") ? "访问 __JY_0 并阅读" :
            text.Contains("https://") ? "请访问 https://example.com 并阅读。" : "另一个句子。");
        var result = await fixture.Provider.TranslateDocumentAsync("Visit https://example.com now. Another sentence.");
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(3, fixture.Requests.Count);
        Assert.Contains("https://example.com", result.Text);
        Assert.DoesNotContain("⟦", result.Text);
    }

    [Fact]
    public async Task SourceMarkerLikeIdentifier_DoesNotCollideWithGeneratedPlaceholders()
    {
        await using var fixture = new PipeFixture(text => text.Contains("__JYX_0__")
            ? "参见 __JY_0__，访问 __JYX_0__。" : "不应使用此响应。");
        var result = await fixture.Provider.TranslateDocumentAsync("See __JY_0__ at https://example.com now.");
        Assert.Equal(0, result.FailedCount);
        Assert.Contains("__JY_0__", result.Text);
        Assert.Contains("https://example.com", result.Text);
        Assert.DoesNotContain("__JYX", result.Text);
    }

    [Fact]
    public async Task BadSentence_DoesNotDiscardSuccessfulNeighbours()
    {
        await using var fixture = new PipeFixture(text => text.StartsWith("Bad") ? "�" : "正常译文。");
        var result = await fixture.Provider.TranslateDocumentAsync("First sentence. Bad sentence. Last sentence.");
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(3, result.Units.Count);
        Assert.StartsWith("正常译文。", result.Text);
        Assert.Contains("〔未译〕Bad sentence.", result.Text);
        Assert.EndsWith("正常译文。", result.Text);
    }

    [Fact]
    public async Task RepeatedNumbers_CannotSilentlyDisappear()
    {
        await using var fixture = new PipeFixture(_ => "共有 42 个项目。");
        var result = await fixture.Provider.TranslateDocumentAsync("There are 42 items and 42 more.");
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("42 items and 42 more", result.Text);
    }

    [Fact]
    public async Task CodeAndUrlsAlone_ArePreservedWithoutModelRequests()
    {
        await using var fixture = new PipeFixture(_ => throw new InvalidOperationException());
        var result = await fixture.Provider.TranslateDocumentAsync("```csharp\nif (x <= 5) return 3.5;\n```\nhttps://example.com");
        Assert.Equal(0, result.FailedCount);
        Assert.Empty(fixture.Requests);
        Assert.Contains("x <= 5", result.Text);
    }

    [Fact]
    public async Task Cancellation_DoesNotReturnOldTranslation()
    {
        await using var fixture = new PipeFixture(_ => "正常译文。", pause: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Provider.TranslateDocumentAsync("A slow request.", cancellationToken: cancellation.Token));
    }

    private sealed class PipeFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "thanslator-pipe-tests", Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        public LocalMtProvider Provider { get; }
        public List<string> Requests { get; } = [];

        public PipeFixture(Func<string, string> respond, bool pause = false)
        {
            Directory.CreateDirectory(_directory);
            foreach (var file in new[] { "host.exe", "ctranslate2.dll", "model.bin", "source.spm", "target.spm" })
                File.WriteAllText(Path.Combine(_directory, file), "fixture");
            var glossary = Path.Combine(_directory, "glossary.json");
            File.WriteAllText(glossary, "{\"entries\":[]}");
            var pipeName = "thanslator-test-" + Guid.NewGuid().ToString("N");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _server = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        ready.TrySetResult();
                        await pipe.WaitForConnectionAsync(_stop.Token);
                        try
                        {
                            while (pipe.IsConnected)
                            {
                                var header = new byte[4];
                                await pipe.ReadExactlyAsync(header, _stop.Token);
                                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                                Assert.InRange(length, 1, 4096);
                                var bytes = new byte[length];
                                await pipe.ReadExactlyAsync(bytes, _stop.Token);
                                var text = Encoding.UTF8.GetString(bytes);
                                Requests.Add(text);
                                if (pause) await Task.Delay(Timeout.Infinite, _stop.Token);
                                var response = Encoding.UTF8.GetBytes(respond(text));
                                BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
                                await pipe.WriteAsync(header, _stop.Token);
                                await pipe.WriteAsync(response, _stop.Token);
                                await pipe.FlushAsync(_stop.Token);
                            }
                        }
                        catch (IOException) { }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
            ready.Task.GetAwaiter().GetResult();
            Provider = new LocalMtProvider(new LocalMtOptions(Path.Combine(_directory, "host.exe"), _directory, pipeName, GlossaryPath: glossary));
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await _stop.CancelAsync();
            await _server.WaitAsync(TimeSpan.FromSeconds(2));
            _stop.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
