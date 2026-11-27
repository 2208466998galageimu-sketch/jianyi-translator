using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Thanslator.Core;

namespace Thanslator.Translation;

/// <summary>Only app-owned servers accept app-initiated downloads, so their storage path is known.</summary>
public sealed class ManagedOllama : IDisposable
{
    public static ManagedOllama Shared { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (Process Process, string Address)> _servers = new(StringComparer.OrdinalIgnoreCase);

    public string? RootForAddress(string address) => _servers.FirstOrDefault(item =>
        item.Value.Address == address && !item.Value.Process.HasExited).Key;

    public async Task<string> EnsureAsync(string directory, string? executable, CancellationToken token)
    {
        var root = Path.GetFullPath(directory);
        await _gate.WaitAsync(token);
        try
        {
            if (_servers.TryGetValue(root, out var server))
            {
                if (!server.Process.HasExited) return server.Address;
                if (_servers.TryRemove(root, out var exited)) exited.Process.Dispose();
            }
            var runtime = FindExecutable(executable);
            if (runtime is null) throw new ArgumentException("请先下载本机运行组件，再下载模型。");
            Directory.CreateDirectory(Path.Combine(root, "llm"));
            Directory.CreateDirectory(Path.Combine(root, "runtime-data"));
            Directory.CreateDirectory(Path.Combine(root, "temporary"));
            int port;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var address = $"http://127.0.0.1:{port}/v1";
            var start = new ProcessStartInfo(runtime) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(runtime)!
            };
            start.ArgumentList.Add("serve");
            start.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
            start.Environment["OLLAMA_MODELS"] = Path.Combine(root, "llm");
            start.Environment["USERPROFILE"] = Path.Combine(root, "runtime-data");
            start.Environment["TEMP"] = Path.Combine(root, "temporary");
            start.Environment["TMP"] = Path.Combine(root, "temporary");
            var process = Process.Start(start) ?? throw new IOException("无法启动本机运行组件。");
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var http = LlmTranslationClient.CreateHttpClient(true);
            http.Timeout = TimeSpan.FromSeconds(2);
            try
            {
                var startupTime = Stopwatch.StartNew();
                while (startupTime.Elapsed < TimeSpan.FromSeconds(20))
                {
                    token.ThrowIfCancellationRequested();
                    if (process.HasExited) throw new IOException("本机运行组件启动失败，请检查组件文件。");
                    try
                    {
                        using var ready = await http.GetAsync(new Uri(LocalModelDownloads.ResolveRoot(address), "api/tags"), token);
                        if (ready.IsSuccessStatusCode && !process.HasExited)
                        {
                            _servers[root] = (process, address);
                            return address;
                        }
                    }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                    await Task.Delay(150, token);
                }
                throw new IOException("本机运行组件启动超时。");
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose(); throw;
            }
        }
        finally { _gate.Release(); }
    }

    public static string? FindExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), "ollama.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        return File.Exists(installed) ? installed : null;
    }

    // https://github.com/ollama/ollama/releases/expanded_assets/v0.40.18
    private static readonly ModelArtifact RecommendedRuntime = new(
        "ollama-ca97292ac1e9acc77fcf473bb2741fa6acd12e3798f261b419680068b7cc4c16.zip",
        "https://github.com/ollama/ollama/releases/download/v0.40.18/ollama-windows-amd64.zip",
        1_717_307_988, "ca97292ac1e9acc77fcf473bb2741fa6acd12e3798f261b419680068b7cc4c16");

    public static async Task<ModelArtifact> GetRuntimeArtifactAsync(string directory, CancellationToken token,
        bool reuseExtracted = false, bool checkForUpdate = false)
    {
        var root = Path.GetFullPath(directory);
        var downloadRoot = ModelStorage.ManagedDirectory(root, Path.Combine(root, "downloads"));
        var cached = await ReadPendingRuntimeAsync(root, downloadRoot, reuseExtracted, token);
        if (cached is not null) return cached;
        if (!checkForUpdate)
        {
            await SaveRuntimeArtifactAsync(downloadRoot, RecommendedRuntime, token);
            return RecommendedRuntime;
        }

        using var http = ModelFileDownload.CreateClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var release = await http.GetAsync("https://api.github.com/repos/ollama/ollama/releases/latest",
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (release.StatusCode == HttpStatusCode.Forbidden &&
                release.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"))
                throw new IOException("GitHub 发布接口达到请求上限，请稍后重试。已下载的文件会保留。");
            release.EnsureSuccessStatusCode();
            await release.Content.LoadIntoBufferAsync(4 * 1024 * 1024, deadline.Token);
            using var metadata = JsonDocument.Parse(await release.Content.ReadAsByteArrayAsync(deadline.Token));
            foreach (var asset in metadata.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != "ollama-windows-amd64.zip") continue;
                var url = asset.GetProperty("browser_download_url").GetString();
                var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
                var size = asset.GetProperty("size").GetInt64();
                if (url is null || digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal)) continue;
                var artifact = new ModelArtifact($"ollama-{digest[7..]}.zip", url, size, digest[7..]);
                if (!ValidRuntimeArtifact(artifact)) continue;
                await SaveRuntimeArtifactAsync(downloadRoot, artifact, token);
                return artifact;
            }
            throw new InvalidDataException("官方版本没有可校验的 Windows x64 运行组件。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("读取运行组件信息超时，请重试。");
        }
    }

    private const string PendingRuntimeFile = "ollama-download.json";

    private static bool ValidRuntimeArtifact(ModelArtifact artifact) =>
        artifact.Sha256 is { Length: 64 } && artifact.Sha256.All(Uri.IsHexDigit) &&
        artifact.Path == $"ollama-{artifact.Sha256}.zip" &&
        artifact.Size is > 0 and <= 4L * 1024 * 1024 * 1024 &&
        Uri.TryCreate(artifact.Url, UriKind.Absolute, out var address) && address.Scheme == "https" &&
        address.Host == "github.com" && address.IsDefaultPort && address.UserInfo.Length == 0 &&
        address.Query.Length == 0 && address.Fragment.Length == 0 &&
        address.AbsolutePath.StartsWith("/ollama/ollama/releases/download/", StringComparison.Ordinal) &&
        address.AbsolutePath.EndsWith("/ollama-windows-amd64.zip", StringComparison.Ordinal);

    private static async Task<ModelArtifact?> ReadPendingRuntimeAsync(string root, string downloads,
        bool reuseExtracted, CancellationToken token)
    {
        var manifest = new FileInfo(Path.Combine(downloads, PendingRuntimeFile));
        if (!manifest.Exists || manifest.Length > 4096 || (manifest.Attributes & FileAttributes.ReparsePoint) != 0)
            return null;

        ModelArtifact? artifact;
        try { artifact = JsonSerializer.Deserialize<ModelArtifact>(await File.ReadAllTextAsync(manifest.FullName, token)); }
        catch (JsonException) { return null; }
        if (artifact is null || !ValidRuntimeArtifact(artifact)) return null;

        var footprint = ModelFileDownload.Inspect(downloads, [artifact]);
        if (footprint.Cached || footprint.CanResume || reuseExtracted && File.Exists(RuntimeDestination(root, artifact)))
            return artifact;
        return null;
    }

    private static async Task SaveRuntimeArtifactAsync(string downloads, ModelArtifact artifact, CancellationToken token)
    {
        Directory.CreateDirectory(downloads);
        var temporary = Path.Combine(downloads, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(artifact), token);
            File.Move(temporary, Path.Combine(downloads, PendingRuntimeFile), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string RuntimeDestination(string directory, ModelArtifact artifact) =>
        Path.Combine(directory, "runtimes", Path.GetFileNameWithoutExtension(artifact.Path), "ollama.exe");

    public static async Task<string> DownloadRuntimeAsync(string directory, IProgress<string>? progress,
        CancellationToken token, ModelArtifact? artifact = null)
    {
        var root = Path.GetFullPath(directory);
        artifact ??= await GetRuntimeArtifactAsync(root, token);
        var runtime = RuntimeDestination(root, artifact);
        _ = ModelStorage.ManagedDirectory(root, runtime);
        if (File.Exists(runtime)) return runtime;

        using var http = ModelFileDownload.CreateClient();
        var downloads = Path.Combine(root, "downloads");
        await ModelFileDownload.EnsureAsync(http, downloads, artifact, progress, token);
        var zipPath = Path.Combine(downloads, artifact.Path);
        const long maxExpanded = 12L * 1024 * 1024 * 1024;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var bytes = archive.Entries.Sum(entry => entry.Length);
            if (bytes > maxExpanded) throw new InvalidDataException("组件解压大小异常。");
            ModelFileDownload.RequireSpace(root, bytes);
        }

        var destination = Path.GetDirectoryName(runtime)!;
        var staging = ModelStorage.ManagedDirectory(root, destination + ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report("正在解压运行组件…");
            await ModelArchive.ExtractAsync(zipPath, staging, token, maxExpanded);
            if (!File.Exists(Path.Combine(staging, "ollama.exe")))
                throw new InvalidDataException("组件中缺少 Ollama 主程序。");
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    _ = ModelStorage.Files(staging, CancellationToken.None);
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }

        try { File.Delete(zipPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return runtime;
    }

    public void Dispose()
    {
        foreach (var server in _servers.Values)
        {
            try { if (!server.Process.HasExited) server.Process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            server.Process.Dispose();
        }
        _servers.Clear();
    }
}
