using Microsoft.Data.Sqlite;
using Thanslator.Core;

namespace Thanslator.Storage;

/// <summary>Persistent domain terminology; legacy personal_terms is never modified here.</summary>
public sealed class KnowledgeRepository(string databasePath)
{
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, ForeignKeys = true }.ToString());
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS knowledge_libraries (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, domain TEXT NOT NULL,
                source_lang TEXT NOT NULL, target_lang TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS knowledge_terms (
                id TEXT PRIMARY KEY, library_id TEXT NOT NULL REFERENCES knowledge_libraries(id) ON DELETE CASCADE,
                term TEXT NOT NULL, normalized_term TEXT NOT NULL, translation TEXT NOT NULL,
                note TEXT NOT NULL, enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
                UNIQUE(library_id, normalized_term)
            );
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<IReadOnlyList<KnowledgeLibrary>> LoadAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction();
        var libraries = new List<KnowledgeLibrary>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,name,domain,source_lang,target_lang FROM knowledge_libraries ORDER BY name,id;";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                libraries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), []));
        }
        var terms = new Dictionary<string, List<KnowledgeTerm>>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT library_id,id,term,translation,note,enabled FROM knowledge_terms ORDER BY normalized_term;";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetString(0);
                if (!terms.TryGetValue(id, out var list)) terms[id] = list = [];
                list.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5)));
            }
        }
        await transaction.CommitAsync(token);
        return libraries.Select(l => l with { Terms = terms.GetValueOrDefault(l.Id) ?? [] }).ToArray();
    }

    // One atomic library snapshot supports manual edits and confirmed import batches alike.
    public async Task SaveAsync(KnowledgeLibrary library, CancellationToken token = default)
    {
        KnowledgeRules.ValidateLibrary(library);
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO knowledge_libraries VALUES($id,$name,$domain,$source,$target)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,domain=excluded.domain,
                    source_lang=excluded.source_lang,target_lang=excluded.target_lang;
                DELETE FROM knowledge_terms WHERE library_id=$id;
                """;
            command.Parameters.AddWithValue("$id", library.Id);
            command.Parameters.AddWithValue("$name", library.Name.Trim());
            command.Parameters.AddWithValue("$domain", library.Domain.Trim());
            command.Parameters.AddWithValue("$source", library.SourceLang.Trim());
            command.Parameters.AddWithValue("$target", library.TargetLang.Trim());
            await command.ExecuteNonQueryAsync(token);
        }
        foreach (var term in library.Terms)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO knowledge_terms VALUES($id,$library,$term,$normalized,$translation,$note,$enabled);";
            command.Parameters.AddWithValue("$id", term.Id);
            command.Parameters.AddWithValue("$library", library.Id);
            command.Parameters.AddWithValue("$term", term.Term.Trim());
            command.Parameters.AddWithValue("$normalized", KnowledgeRules.Normalize(term.Term));
            command.Parameters.AddWithValue("$translation", term.Translation.Trim());
            command.Parameters.AddWithValue("$note", term.Note.Trim());
            command.Parameters.AddWithValue("$enabled", term.Enabled);
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    public async Task ImportAsync(string libraryId, TermImportPreview preview, CancellationToken token = default)
    {
        if (!preview.CanImport) throw new ArgumentException("请先处理导入预览中的错误。");
        // Include existing data in the same write transaction; a stale preview cannot overwrite newer edits.
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction();
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM knowledge_terms WHERE library_id=$library;";
            check.Parameters.AddWithValue("$library", libraryId);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(token)) + preview.Terms.Count > KnowledgeRules.MaxTerms)
                throw new ArgumentException("每库最多 5000 条术语。");
        }
        foreach (var term in preview.Terms)
        {
            KnowledgeRules.ValidateTerm(term);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO knowledge_terms VALUES($id,$library,$term,$normalized,$translation,$note,$enabled);";
            command.Parameters.AddWithValue("$id", term.Id);
            command.Parameters.AddWithValue("$library", libraryId);
            command.Parameters.AddWithValue("$term", term.Term.Trim());
            command.Parameters.AddWithValue("$normalized", KnowledgeRules.Normalize(term.Term));
            command.Parameters.AddWithValue("$translation", term.Translation.Trim());
            command.Parameters.AddWithValue("$note", term.Note.Trim());
            command.Parameters.AddWithValue("$enabled", term.Enabled);
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    public async Task DeleteAsync(string libraryId, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM knowledge_libraries WHERE id=$id;";
        command.Parameters.AddWithValue("$id", libraryId);
        await command.ExecuteNonQueryAsync(token);
    }
}
