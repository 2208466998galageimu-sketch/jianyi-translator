using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Thanslator.Capture;
using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class ModelImportBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jianyi-boundary-" + Guid.NewGuid().ToString("N"));
    public ModelImportBoundaryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("../det.onnx")]
    [InlineData("C:/det.onnx")]
    [InlineData("/det.onnx")]
    [InlineData("dir//det.onnx")]
    public void OcrProfileCannotReferenceOutsidePackage(string path)
    {
        File.WriteAllText(Path.Combine(_root,"ocr.json"),JsonSerializer.Serialize(new { formatVersion=1, engine="paddle-ctc",detector=path,recognizer="rec.onnx",characters="dict.txt" }));
        Assert.Throws<InvalidDataException>(() => OcrProfile.Read(_root));
    }

    [Fact]
    public async Task GgufZipRejectsAmbiguousModels()
    {
        var zip = Path.Combine(_root,"models.zip");
        using (var archive = ZipFile.Open(zip,ZipArchiveMode.Create))
            foreach (var name in new[] {"one.gguf","two.gguf"})
            { using var stream=archive.CreateEntry(name).Open(); stream.Write(new byte[24]); }
        await Assert.ThrowsAsync<InvalidDataException>(()=>ModelArchive.ExtractGgufAsync(zip,Path.Combine(_root,"out"),default));
    }

    [Fact]
    public async Task ModelZipRejectsTraversalWithoutTouchingOutsideFile()
    {
        var zip=Path.Combine(_root,"model.zip");
        using (var archive=ZipFile.Open(zip,ZipArchiveMode.Create))
        { using var stream=archive.CreateEntry("../outside.txt").Open(); stream.WriteByte(1); }
        await Assert.ThrowsAsync<InvalidDataException>(()=>ModelArchive.ExtractAsync(zip,Path.Combine(_root,"out"),default));
        Assert.False(File.Exists(Path.Combine(_root,"outside.txt")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadResumesOrRestartsWhenServerIgnoresRange(bool supportsRange)
    {
        byte[] bytes=[1,2,3,4,5,6];
        var artifact=new ModelArtifact("model.bin","https://models.example/model",bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)));
        await File.WriteAllBytesAsync(Path.Combine(_root,"model.bin.download"),bytes[..2]);
        using var http=new HttpClient(new Handler(request =>
        {
            Assert.Equal(2,request.Headers.Range!.Ranges.Single().From);
            var response=new HttpResponseMessage(supportsRange?HttpStatusCode.PartialContent:HttpStatusCode.OK)
            { Content=new ByteArrayContent(supportsRange?bytes[2..]:bytes) };
            if (supportsRange) response.Content.Headers.ContentRange=new ContentRangeHeaderValue(2,bytes.Length-1,bytes.Length);
            return response;
        }));
        await ModelFileDownload.EnsureAsync(http,_root,artifact,null,default);
        Assert.Equal(bytes,await File.ReadAllBytesAsync(Path.Combine(_root,"model.bin")));
        Assert.False(File.Exists(Path.Combine(_root,"model.bin.download")));
    }

    [Fact]
    public async Task CorruptDownloadNeverBecomesActiveModel()
    {
        using var http=new HttpClient(new Handler(_=>new(HttpStatusCode.OK) { Content=new ByteArrayContent([1,2,3]) }));
        await Assert.ThrowsAsync<InvalidDataException>(()=>ModelFileDownload.EnsureAsync(http,_root,
            new("model.bin","https://models.example/model",3,new string('0',64)),null,default));
        Assert.False(File.Exists(Path.Combine(_root,"model.bin")));
    }

    [Fact]
    public async Task InterruptedFullResponsePreservesTheLongerCheckpoint()
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        var artifact = new ModelArtifact("model.bin", "https://models.example/model", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var partial = Path.Combine(_root, artifact.Path + ".download");
        await File.WriteAllBytesAsync(partial, bytes[..4]);
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(4, request.Headers.Range?.Ranges.Single().From);
            requests++;
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes[..2]) { Headers = { ContentLength = bytes.Length } }
            };
        }));

        await Assert.ThrowsAsync<IOException>(() => ModelFileDownload.EnsureAsync(http, _root, artifact, null, default));
        Assert.Equal(3, requests);
        Assert.Equal(bytes[..4], await File.ReadAllBytesAsync(partial));
        Assert.False(File.Exists(Path.Combine(_root, artifact.Path)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedRangeKeepsCheckpointForTheNextRequest(bool recovers)
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        var artifact = new ModelArtifact("model.bin", "https://models.example/model", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var partial = Path.Combine(_root, artifact.Path + ".download");
        await File.WriteAllBytesAsync(partial, bytes[..2]);
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
            if (++requests < 3 || !recovers) return new(HttpStatusCode.RequestedRangeNotSatisfiable);
            return new(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes[2..])
                {
                    Headers = { ContentRange = new(2, bytes.Length - 1, bytes.Length) }
                }
            };
        }));

        if (recovers)
        {
            await ModelFileDownload.EnsureAsync(http, _root, artifact, null, default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, artifact.Path)));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() =>
                ModelFileDownload.EnsureAsync(http, _root, artifact, null, default));
            Assert.Equal(bytes[..2], await File.ReadAllBytesAsync(partial));
            Assert.False(File.Exists(Path.Combine(_root, artifact.Path)));
        }
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task InvalidRangeDoesNotWriteOverCheckpoint()
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        var artifact = new ModelArtifact("model.bin", "https://models.example/model", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var partial = Path.Combine(_root, artifact.Path + ".download");
        await File.WriteAllBytesAsync(partial, bytes[..2]);
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(bytes[1..])
            {
                Headers = { ContentRange = new(1, bytes.Length - 1, bytes.Length) }
            }
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ModelFileDownload.EnsureAsync(http, _root, artifact, null, default));
        Assert.Equal(bytes[..2], await File.ReadAllBytesAsync(partial));
        Assert.False(File.Exists(Path.Combine(_root, artifact.Path)));
    }

    [Fact]
    public async Task InterruptedDownloadKeepsPartialProgressAndCancellationDoesNotActivateIt()
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        var artifact = new ModelArtifact("model.bin", "https://models.example/model", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var partial = Path.Combine(_root, "model.bin.download");
        await File.WriteAllBytesAsync(partial, bytes[..2]);
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModelFileDownload.EnsureAsync(http, _root, artifact, null, cancellation.Token));
        var footprint = ModelFileDownload.Inspect(_root, [artifact]);
        Assert.True(footprint.CanResume);
        Assert.False(footprint.Cached);
        Assert.Equal(4, footprint.RemainingBytes);
        Assert.Equal(bytes[..2], await File.ReadAllBytesAsync(partial));
        Assert.False(File.Exists(Path.Combine(_root, "model.bin")));

        using var resume = new HttpClient(new Handler(request =>
        {
            Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
            return new(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes[2..])
                {
                    Headers = { ContentRange = new(2, bytes.Length - 1, bytes.Length) }
                }
            };
        }));
        await ModelFileDownload.EnsureAsync(resume, _root, artifact, null, default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, artifact.Path)));
    }

    [Fact]
    public async Task UnavailableSourceFailsWithoutRetryAndInsufficientDiskFailsBeforeWrite()
    {
        var artifact = new ModelArtifact("model.bin", "https://models.example/model", 10, new string('0', 64));
        var requests = 0;
        using var http = new HttpClient(new Handler(_ => { requests++; return new(HttpStatusCode.NotFound); }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ModelFileDownload.EnsureAsync(http, _root, artifact, null, default));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(1, requests);
        Assert.Throws<IOException>(() => ModelFileDownload.RequireSpace(_root, long.MaxValue));
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task TransientTimeoutRetriesAndPublishesOnlyVerifiedBytes()
    {
        byte[] bytes = [4, 5, 6];
        var artifact = new ModelArtifact("model.bin", "https://models.example/model", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++requests == 1) throw new OperationCanceledException();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        await ModelFileDownload.EnsureAsync(http, _root, artifact, null, default);
        Assert.Equal(2, requests);
        Assert.True(ModelFileDownload.Inspect(_root, [artifact]).Cached);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, artifact.Path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedRuntimeArchiveIsReusedAndPublishedOnlyAfterExtraction(bool cancelBeforeExtraction)
    {
        var downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloads);
        var zip = Path.Combine(downloads, "runtime.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var executable = archive.CreateEntry("ollama.exe").Open())
            executable.Write([1, 2, 3]);
        var bytes = await File.ReadAllBytesAsync(zip);
        var artifact = new ModelArtifact("runtime.zip", "https://models.example/runtime", bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)));
        if (cancelBeforeExtraction)
        {
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(message =>
            {
                if (message.StartsWith("正在解压", StringComparison.Ordinal)) cancellation.Cancel();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ManagedOllama.DownloadRuntimeAsync(_root, progress, cancellation.Token, artifact));
            Assert.True(File.Exists(zip));
            Assert.False(File.Exists(ManagedOllama.RuntimeDestination(_root, artifact)));
            var runtimes = Path.Combine(_root, "runtimes");
            Assert.False(Directory.Exists(runtimes) && Directory.EnumerateDirectories(runtimes, "*.staging-*").Any());
        }
        var installed = await ManagedOllama.DownloadRuntimeAsync(_root, null, default, artifact);
        Assert.Equal(ManagedOllama.RuntimeDestination(_root, artifact), installed);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(installed));
        Assert.False(File.Exists(zip));
        Assert.Equal(installed, await ManagedOllama.DownloadRuntimeAsync(_root, null, default, artifact));
    }

    [Fact]
    public void DownloadClientIdentifiesJianyiToArtifactServers()
    {
        using var client = ModelFileDownload.CreateClient();
        Assert.Contains(client.DefaultRequestHeaders.UserAgent, item => item.Product?.Name == "Jianyi");
    }

    [Fact]
    public async Task PendingRuntimeReusesReleaseMetadataAfterRestart()
    {
        var downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloads);
        var digest = new string('a', 64);
        var artifact = new ModelArtifact($"ollama-{digest}.zip",
            "https://github.com/ollama/ollama/releases/download/v0.40.18/ollama-windows-amd64.zip", 12, digest);
        await File.WriteAllTextAsync(Path.Combine(downloads, "ollama-download.json"), JsonSerializer.Serialize(artifact));
        await File.WriteAllBytesAsync(Path.Combine(downloads, artifact.Path + ".download"), [1, 2, 3]);

        var resumed = await ManagedOllama.GetRuntimeArtifactAsync(_root, default);
        Assert.Equal(artifact, resumed);
        Assert.Equal(9, ModelFileDownload.Inspect(downloads, [resumed]).RemainingBytes);
    }

    [Fact]
    public async Task RecommendedRuntimePublishesPinnedOfficialMetadataBeforeTransfer()
    {
        var artifact = await ManagedOllama.GetRuntimeArtifactAsync(_root, default);
        var manifest = Path.Combine(_root, "downloads", "ollama-download.json");
        Assert.Equal(artifact, JsonSerializer.Deserialize<ModelArtifact>(await File.ReadAllTextAsync(manifest)));
        Assert.Equal("github.com", new Uri(artifact.Url).Host);
        Assert.Equal(artifact.Size, ModelFileDownload.Inspect(Path.GetDirectoryName(manifest)!, [artifact]).RemainingBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryBudgetOnlyCountsConsecutiveAttemptsWithoutProgress(bool advances)
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        var artifact = new ModelArtifact("model.bin", "https://models.example/model", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var partial = Path.Combine(_root, artifact.Path + ".download");
        await File.WriteAllBytesAsync(partial, bytes[..1]);
        var offsets = new List<long>();
        using var http = new HttpClient(new Handler(request =>
        {
            var offset = (int)request.Headers.Range!.Ranges.Single().From!.Value;
            offsets.Add(offset);
            if (!advances) return new(HttpStatusCode.ServiceUnavailable);
            return new(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes[offset..(offset + 1)])
                {
                    Headers = { ContentRange = new(offset, bytes.Length - 1, bytes.Length),
                        ContentLength = bytes.Length - offset }
                }
            };
        }));

        if (advances)
        {
            await ModelFileDownload.EnsureAsync(http, _root, artifact, null, default);
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, offsets);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, artifact.Path)));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() =>
                ModelFileDownload.EnsureAsync(http, _root, artifact, null, default));
            Assert.Equal(new long[] { 1, 1, 1 }, offsets);
            Assert.Equal(bytes[..1], await File.ReadAllBytesAsync(partial));
        }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(reply(request));
    }
}
