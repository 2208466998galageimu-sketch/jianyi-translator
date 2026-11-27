using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Thanslator.Core;

namespace Thanslator.Storage;

/// <summary>
/// SQLite-backed settings store. Settings are persisted as one JSON value per
/// key so adding a toggle does not require a schema migration.
/// </summary>
public sealed class SettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _dbPath;

    public SettingsRepository(string? dbPath = null)
    {
        _dbPath = dbPath ?? DefaultDatabasePath();
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string DatabasePath => _dbPath;

    /// <summary>Settings live in the portable <c>data</c> folder, see <see cref="AppPaths"/>.</summary>
    public static string DefaultDatabasePath() => AppPaths.SettingsDatabase;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at_ms INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS personal_terms (
                source_lang TEXT NOT NULL,
                target_lang TEXT NOT NULL,
                term TEXT NOT NULL,
                gloss TEXT NOT NULL,
                PRIMARY KEY (source_lang, target_lang, term)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return FromMap(await ReadRowsAsync(connection, null, cancellationToken));
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await WriteRowsAsync(connection, transaction, ToMap(settings), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task<Dictionary<string, string>> ReadRowsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT key, value_json FROM settings;";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values[reader.GetString(0)] = reader.GetString(1);
        return values;
    }

    internal static async Task WriteRowsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<KeyValuePair<string, string>> values, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings(key, value_json, updated_at_ms) VALUES ($key, $value, $updated)
            ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at_ms=excluded.updated_at_ms;
            """;
        var keyParameter = command.Parameters.Add("$key", SqliteType.Text);
        var valueParameter = command.Parameters.Add("$value", SqliteType.Text);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var (key, json) in values)
        {
            keyParameter.Value = key;
            valueParameter.Value = json;
            await command.ExecuteNonQueryAsync(token);
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Dictionary<PersonalTermKey, string>> LoadPersonalTermsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_lang, target_lang, term, gloss FROM personal_terms;";
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        var terms = new Dictionary<PersonalTermKey, string>();
        while (await rows.ReadAsync(cancellationToken))
            terms[new(rows.GetString(0), rows.GetString(1), rows.GetString(2))] = rows.GetString(3);
        return terms;
    }

    public async Task SavePersonalTermAsync(PersonalTermKey key, string? gloss,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key.Term) || key.Term.Length > PersonalTermKey.MaxTermLength)
            throw new ArgumentException("Invalid personal term.", nameof(key));
        if (gloss is not null && (string.IsNullOrWhiteSpace(gloss) || gloss.Length > PersonalTermKey.MaxGlossLength))
            throw new ArgumentException("Invalid personal gloss.", nameof(gloss));

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$source", key.SourceLang);
        command.Parameters.AddWithValue("$target", key.TargetLang);
        command.Parameters.AddWithValue("$term", key.Term);
        if (gloss is null)
        {
            command.CommandText = """
                DELETE FROM personal_terms WHERE source_lang = $source AND target_lang = $target AND term = $term;
                """;
        }
        else
        {
            command.Parameters.AddWithValue("$gloss", gloss);
            command.CommandText = """
                INSERT INTO personal_terms(source_lang, target_lang, term, gloss) VALUES ($source, $target, $term, $gloss)
                ON CONFLICT(source_lang, target_lang, term) DO UPDATE SET gloss = excluded.gloss;
                """;
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };

        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    internal static Dictionary<string, string> ToMap(AppSettings settings)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = JsonSerializer.Serialize(settings.Enabled, JsonOptions),
            ["literalMode"] = JsonSerializer.Serialize(settings.LiteralMode, JsonOptions),
            ["includePhrases"] = JsonSerializer.Serialize(settings.IncludePhrases, JsonOptions),
            ["surfaceMode"] = JsonSerializer.Serialize(settings.SurfaceMode, JsonOptions),
            ["dwellMs"] = JsonSerializer.Serialize(settings.DwellMs, JsonOptions),
            ["panelAutoCloseMs"] = JsonSerializer.Serialize(settings.PanelAutoCloseMs, JsonOptions),
            ["sourceLang"] = JsonSerializer.Serialize(settings.SourceLang, JsonOptions),
            ["targetLang"] = JsonSerializer.Serialize(settings.TargetLang, JsonOptions),
            ["offlineMode"] = JsonSerializer.Serialize(settings.OfflineMode, JsonOptions),
            ["translationMode"] = JsonSerializer.Serialize(settings.TranslationMode, JsonOptions),
            ["knowledgeLibraryIds"] = JsonSerializer.Serialize(settings.KnowledgeLibraryIds, JsonOptions),
            ["llmApi"] = JsonSerializer.Serialize(settings.LlmApi, JsonOptions),
            ["llmLocal"] = JsonSerializer.Serialize(settings.LlmLocal, JsonOptions),
            ["localMtEnabled"] = JsonSerializer.Serialize(settings.LocalMtEnabled, JsonOptions),
            ["localMtModelId"] = JsonSerializer.Serialize(settings.LocalMtModelId, JsonOptions),
            ["localMtModelPath"] = JsonSerializer.Serialize(settings.LocalMtModelPath, JsonOptions),
            ["modelDownloadDirectory"] = JsonSerializer.Serialize(settings.ModelDownloadDirectory, JsonOptions),
            ["ollamaRuntimePath"] = JsonSerializer.Serialize(settings.OllamaRuntimePath, JsonOptions),
            ["managedLlmStorageRoot"] = JsonSerializer.Serialize(settings.ManagedLlmStorageRoot, JsonOptions),
            ["localMtThreads"] = JsonSerializer.Serialize(settings.LocalMtThreads, JsonOptions),
            ["localMtModelStatus"] = JsonSerializer.Serialize(settings.LocalMtModelStatus, JsonOptions),
            ["allowUnofficialProviders"] = JsonSerializer.Serialize(settings.AllowUnofficialProviders, JsonOptions),
            ["clipboardFallback"] = JsonSerializer.Serialize(settings.ClipboardFallback, JsonOptions),
            ["ocrShortWords"] = JsonSerializer.Serialize(settings.OcrShortWords, JsonOptions),
            ["ocrMediumWords"] = JsonSerializer.Serialize(settings.OcrMediumWords, JsonOptions),
            ["ocrShortSeconds"] = JsonSerializer.Serialize(settings.OcrShortSeconds, JsonOptions),
            ["ocrMediumSeconds"] = JsonSerializer.Serialize(settings.OcrMediumSeconds, JsonOptions),
            ["ocrLongSeconds"] = JsonSerializer.Serialize(settings.OcrLongSeconds, JsonOptions),
            ["ocrEnabled"] = JsonSerializer.Serialize(settings.OcrEnabled, JsonOptions),
            ["ocrComponentPath"] = JsonSerializer.Serialize(settings.OcrComponentPath, JsonOptions),
            ["hotkey"] = JsonSerializer.Serialize(settings.Hotkey, JsonOptions),
            ["theme"] = JsonSerializer.Serialize(settings.Theme, JsonOptions),
            ["launchAtStartup"] = JsonSerializer.Serialize(settings.LaunchAtStartup, JsonOptions),
            ["historyEnabled"] = JsonSerializer.Serialize(settings.HistoryEnabled, JsonOptions),
            ["uiLocale"] = JsonSerializer.Serialize(settings.UiLocale, JsonOptions),
            ["popperLeft"] = JsonSerializer.Serialize(settings.PopperLeft, JsonOptions),
            ["popperTop"] = JsonSerializer.Serialize(settings.PopperTop, JsonOptions),
            ["popperDockEdge"] = JsonSerializer.Serialize(settings.PopperDockEdge, JsonOptions),
            ["popperDockOffset"] = JsonSerializer.Serialize(settings.PopperDockOffset, JsonOptions),
            ["popperDrawerOpen"] = JsonSerializer.Serialize(settings.PopperDrawerOpen, JsonOptions)
        };
    }

    internal static AppSettings FromMap(IReadOnlyDictionary<string, string> map)
    {
        var defaults = new AppSettings();
        var mode = Read(map, "translationMode", defaults.TranslationMode);
        return new AppSettings
        {
            Enabled = Read(map, "enabled", defaults.Enabled),
            LiteralMode = Read(map, "literalMode", defaults.LiteralMode),
            IncludePhrases = Read(map, "includePhrases", defaults.IncludePhrases),
            SurfaceMode = Read(map, "surfaceMode", defaults.SurfaceMode),
            DwellMs = Read(map, "dwellMs", defaults.DwellMs),
            PanelAutoCloseMs = Read(map, "panelAutoCloseMs", defaults.PanelAutoCloseMs),
            SourceLang = Read(map, "sourceLang", defaults.SourceLang),
            TargetLang = Read(map, "targetLang", defaults.TargetLang),
            OfflineMode = Read(map, "offlineMode", defaults.OfflineMode),
            TranslationMode = Enum.IsDefined(mode) ? mode : defaults.TranslationMode,
            KnowledgeLibraryIds = Read(map, "knowledgeLibraryIds", defaults.KnowledgeLibraryIds),
            LlmApi = Read(map, "llmApi", defaults.LlmApi),
            LlmLocal = Read(map, "llmLocal", defaults.LlmLocal),
            LocalMtEnabled = Read(map, "localMtEnabled", defaults.LocalMtEnabled),
            LocalMtModelId = Read(map, "localMtModelId", defaults.LocalMtModelId),
            LocalMtModelPath = Read(map, "localMtModelPath", defaults.LocalMtModelPath),
            ModelDownloadDirectory = Read(map, "modelDownloadDirectory", defaults.ModelDownloadDirectory),
            OllamaRuntimePath = Read(map, "ollamaRuntimePath", defaults.OllamaRuntimePath),
            ManagedLlmStorageRoot = Read(map, "managedLlmStorageRoot", defaults.ManagedLlmStorageRoot),
            LocalMtThreads = Read(map, "localMtThreads", defaults.LocalMtThreads),
            LocalMtModelStatus = Read(map, "localMtModelStatus", defaults.LocalMtModelStatus),
            AllowUnofficialProviders = Read(map, "allowUnofficialProviders", defaults.AllowUnofficialProviders),
            ClipboardFallback = Read(map, "clipboardFallback", defaults.ClipboardFallback),
            OcrShortWords = Read(map, "ocrShortWords", defaults.OcrShortWords),
            OcrMediumWords = Read(map, "ocrMediumWords", defaults.OcrMediumWords),
            OcrShortSeconds = Read(map, "ocrShortSeconds", defaults.OcrShortSeconds),
            OcrMediumSeconds = Read(map, "ocrMediumSeconds", defaults.OcrMediumSeconds),
            OcrLongSeconds = Read(map, "ocrLongSeconds", defaults.OcrLongSeconds),
            OcrEnabled = Read(map, "ocrEnabled", defaults.OcrEnabled),
            OcrComponentPath = Read(map, "ocrComponentPath", defaults.OcrComponentPath),
            Hotkey = Read(map, "hotkey", defaults.Hotkey),
            Theme = Read(map, "theme", defaults.Theme),
            LaunchAtStartup = Read(map, "launchAtStartup", defaults.LaunchAtStartup),
            HistoryEnabled = Read(map, "historyEnabled", defaults.HistoryEnabled),
            UiLocale = Read(map, "uiLocale", defaults.UiLocale),
            PopperLeft = Read<double?>(map, "popperLeft", defaults.PopperLeft),
            PopperTop = Read<double?>(map, "popperTop", defaults.PopperTop),
            PopperDockEdge = Read(map, "popperDockEdge", defaults.PopperDockEdge),
            PopperDockOffset = Read(map, "popperDockOffset", defaults.PopperDockOffset),
            PopperDrawerOpen = Read(map, "popperDrawerOpen", defaults.PopperDrawerOpen)
        };
    }

    private static T Read<T>(IReadOnlyDictionary<string, string> map, string key, T fallback)
    {
        if (!map.TryGetValue(key, out var json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
