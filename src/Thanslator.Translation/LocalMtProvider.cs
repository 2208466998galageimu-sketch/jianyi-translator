using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using Thanslator.Core;

namespace Thanslator.Translation;

public sealed record LocalMtOptions(
    string HostPath,
    string ModelDirectory,
    string PipeName = "Thanslator.LocalMt",
    int Threads = 4,
    string? GlossaryPath = null,
    string Architecture = "marian",
    string SourceLanguage = "en",
    string TargetLanguage = "zh-Hans",
    string SourcePrefix = ">>cmn_Hans<<",
    bool AppendSourceEos = false)
{
    public string SourceSpmPath => Path.Combine(ModelDirectory, Architecture == "m2m100" ? "sentencepiece.bpe.model" : "source.spm");
    public string TargetSpmPath => Path.Combine(ModelDirectory, Architecture == "m2m100" ? "sentencepiece.bpe.model" : "target.spm");

    public static string DefaultHostPath() =>
        Path.Combine(AppContext.BaseDirectory, "LocalMtHost", "Thanslator.LocalMtHost.exe");

    public static string DefaultModelDirectory() => AppPaths.DefaultModelDirectory;
}

/// <summary>
/// Starts and talks to the native LocalMtHost over a length-prefixed UTF-8
/// named-pipe protocol. The host owns the model and CTranslate2; this process
/// only sends text and receives translations.
/// </summary>
public sealed class LocalMtProvider : IAsyncDisposable
{
    private readonly LocalMtOptions _options;
    private readonly TranslationGlossary _glossary;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private bool _disposed;

    public LocalMtProvider(LocalMtOptions options)
    {
        _options = options;
        _glossary = options.GlossaryPath is null
            ? (options.SourceLanguage == "en" && options.TargetLanguage == "zh-Hans" ? TranslationGlossary.LoadDefault() : TranslationGlossary.Empty)
            : TranslationGlossary.Load(options.GlossaryPath);
    }

    public LocalMtOptions Options => _options;

    public bool IsConfigured => IsConfiguredOptions(_options);

    public static bool IsConfiguredOptions(LocalMtOptions options) =>
        File.Exists(options.HostPath) &&
        File.Exists(Path.Combine(Path.GetDirectoryName(options.HostPath) ?? "", "ctranslate2.dll")) &&
        File.Exists(Path.Combine(options.ModelDirectory, "model.bin")) &&
        File.Exists(options.SourceSpmPath) &&
        File.Exists(options.TargetSpmPath);

    public string StatusText => IsConfigured
        ? "本地 MT 已安装"
        : "本地 MT 文件不完整";

    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
        => (await TranslateDocumentAsync(text, cancellationToken: cancellationToken)).Text;

    /// <summary>按句翻译，保留局部失败；调用方可展示已完成单元并取消整次请求。</summary>
    public async Task<TranslationDocument> TranslateDocumentAsync(string text,
        IProgress<TranslationDocument>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var units = new List<TranslationUnit>();
            foreach (var source in TranslationSegmenter.Split(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trimmed = source.Trim();
                var protection = TextProtector.Protect(trimmed);
                if (trimmed.Length == 0 || protection.Spans.Any(span => span.Text == trimmed))
                {
                    units.Add(new TranslationUnit(source, source));
                }
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        var normalized = string.Concat(trimmed.Select(c => char.IsWhiteSpace(c) ? ' ' : c));
                        var translation = await TranslateSentenceAsync(normalized, timeout.Token);
                        var left = source.Length - source.TrimStart().Length;
                        var right = source.TrimEnd().Length;
                        units.Add(new TranslationUnit(source, source[..left] + translation + source[right..]));
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        ResetConnection();
                        await StopOwnedHostAsync();
                        units.Add(new TranslationUnit(source, null, "localmt.timeout"));
                    }
                    catch (Exception ex) when (ex is LocalMtException or IOException)
                    {
                        ResetConnection();
                        units.Add(new TranslationUnit(source, null, "localmt.invalid_output"));
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new TranslationDocument(units.ToArray()));
            }
            return new TranslationDocument(units);
        }
        catch (OperationCanceledException)
        {
            ResetConnection();
            await StopOwnedHostAsync();
            throw;
        }
        catch (IOException)
        {
            ResetConnection();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> TranslateSentenceAsync(string text, CancellationToken cancellationToken)
    {
        var protection = TextProtector.Protect(text, protectNumbers: false);
        var terms = _glossary.ProtectTerms(protection.MaskedText);
        // OPUS-MT 实测可保留 ASCII 标记，Unicode 方括号会被改坏；仍逐个校验身份和次数。
        var prefix = "__JY";
        while (text.Contains(prefix, StringComparison.Ordinal)) prefix += "X";
        var placeholders = protection.Spans.Select(span => $"⟦{span.Index}⟧")
            .Concat(terms.Spans.Select(span => $"⟦T{span.Index}⟧")).ToArray();
        var wireText = terms.Text;
        for (var index = 0; index < placeholders.Length; index++)
            wireText = wireText.Replace(placeholders[index], $"{prefix}_{index}__", StringComparison.Ordinal);
        string translated;
        try { translated = await SendAsync(wireText, cancellationToken); }
        catch (LocalMtException ex) when (ex.Message == "localmt.host_error" && terms.Text != text)
        {
            translated = string.Empty;
        }
        var intact = true;
        for (var index = 0; index < placeholders.Length; index++)
        {
            var marker = $"{prefix}_{index}__";
            if (TextProtector.CountOccurrences(translated, marker) != 1) intact = false;
            translated = translated.Replace(marker, placeholders[index], StringComparison.Ordinal);
        }
        if (intact && !translated.Contains(prefix, StringComparison.Ordinal) &&
            _glossary.TryRestoreTerms(translated, terms, out var restoredTerms) &&
            TextProtector.TryRestore(restoredTerms, protection, out var restored) && IsValid(text, restored))
        {
            return _glossary.Apply(text, restored);
        }

        // 保护符被模型改写时只重试一次原句；仍不完整就保留该句原文。
        if (!string.Equals(terms.Text, text, StringComparison.Ordinal))
        {
            translated = await SendAsync(text, cancellationToken);
            if (IsValid(text, translated)) return _glossary.Apply(text, translated);
        }
        throw new LocalMtException("localmt.invalid_output");
    }

    private bool IsValid(string source, string translation) =>
        LocalMtGuard.ValidateOutput(source, translation) is null &&
        TextProtector.IsComplete(translation, TextProtector.Protect(source)) &&
        _glossary.AreTermsComplete(source, translation);

    private async Task<string> SendAsync(string text, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await EnsureConnectedAsync(cancellationToken);
        var payload = System.Text.Encoding.UTF8.GetBytes(text);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await _pipe!.WriteAsync(header, cancellationToken);
        await _pipe.WriteAsync(payload, cancellationToken);
        await _pipe.FlushAsync(cancellationToken);
        await _pipe.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > 4 * 1024 * 1024) throw new LocalMtException("localmt.invalid_response");
        var response = new byte[length];
        await _pipe.ReadExactlyAsync(response, cancellationToken);
        var translated = System.Text.Encoding.UTF8.GetString(response).Trim();
        if (translated.StartsWith("__ERROR__:", StringComparison.Ordinal)) throw new LocalMtException("localmt.host_error");
        return translated;
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe is { IsConnected: true })
        {
            return;
        }

        ResetConnection();
        if (!IsConfigured)
        {
            throw new LocalMtException("本地 MT 尚未安装或文件不完整");
        }

        // If a host is already running, connect without starting a duplicate.
        var existing = CreatePipe();
        try
        {
            await existing.ConnectAsync(250, cancellationToken);
            _pipe = existing;
            return;
        }
        catch (TimeoutException)
        {
            existing.Dispose();
        }
        catch
        {
            existing.Dispose();
            throw;
        }

        StartHost();
        var pipe = CreatePipe();
        try
        {
            await pipe.ConnectAsync(20_000, cancellationToken);
            _pipe = pipe;
        }
        catch (OperationCanceledException)
        {
            pipe.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            pipe.Dispose();
            ResetConnection();
            throw new LocalMtException("无法连接本地 MT Host", ex);
        }
    }

    private NamedPipeClientStream CreatePipe() =>
        new(".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    private void StartHost()
    {
        if (_process is { HasExited: false })
        {
            return;
        }
        _process?.Dispose();

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.HostPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_options.HostPath) ?? AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_options.ModelDirectory);
        startInfo.ArgumentList.Add("--source-spm");
        startInfo.ArgumentList.Add(_options.SourceSpmPath);
        startInfo.ArgumentList.Add("--target-spm");
        startInfo.ArgumentList.Add(_options.TargetSpmPath);
        startInfo.ArgumentList.Add("--architecture");
        startInfo.ArgumentList.Add(_options.Architecture);
        startInfo.ArgumentList.Add("--source-language");
        startInfo.ArgumentList.Add(_options.SourceLanguage);
        startInfo.ArgumentList.Add("--target-language");
        startInfo.ArgumentList.Add(_options.TargetLanguage);
        startInfo.ArgumentList.Add("--source-prefix");
        startInfo.ArgumentList.Add(_options.SourcePrefix);
        startInfo.ArgumentList.Add("--append-source-eos");
        startInfo.ArgumentList.Add(_options.AppendSourceEos ? "true" : "false");
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(_options.PipeName);
        startInfo.ArgumentList.Add("--threads");
        startInfo.ArgumentList.Add(Math.Max(1, _options.Threads).ToString());

        _process = Process.Start(startInfo)
            ?? throw new LocalMtException("无法启动本地 MT Host");

        // Drain the pipes so a verbose host can never block on stdio.
        _ = _process.StandardOutput.ReadToEndAsync();
        _ = _process.StandardError.ReadToEndAsync();
    }

    private void ResetConnection()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    private async Task StopOwnedHostAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // 只结束自己启动的 Host；外部管道服务不归本实例管理。
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetConnection();

        await _gate.WaitAsync();
        try { await StopOwnedHostAsync(); }
        finally { _gate.Release(); }
    }
}

public sealed class LocalMtException : Exception
{
    public LocalMtException(string message) : base(message)
    {
    }

    public LocalMtException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
