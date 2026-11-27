using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class DatabaseAudit
{
    public static async Task RunAsync(string database, string output)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();
        var result = new Dictionary<string, object>();
        foreach (var name in new[] { "settings", "personal_terms", "knowledge_libraries", "knowledge_terms", "more_settings", "configured_models", "knowledge_usage", "knowledge_requests", "translation_preferences" })
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            exists.Parameters.AddWithValue("$name", name);
            if ((long)(await exists.ExecuteScalarAsync())! == 0) { result[name] = new { exists = false }; continue; }
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + name + " ORDER BY rowid";
            await using var reader = await command.ExecuteReaderAsync();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var count = 0;
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values) + "\n"));
                count++;
            }
            result[name] = new { exists = true, count, hash = Convert.ToHexString(hash.GetHashAndReset()) };
        }
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
