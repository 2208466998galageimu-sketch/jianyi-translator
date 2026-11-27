using Microsoft.Data.Sqlite;
using Thanslator.Dictionary;
using Thanslator.Translation;

namespace Thanslator.Tests;

internal sealed class TestDictionaryFixture : IDisposable
{
    private readonly string _directory;

    private TestDictionaryFixture(string directory) => _directory = directory;

    public static async Task<TestDictionaryFixture> CreateAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "thanslator-dictionary-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "test.db");
        var overridesPath = Path.Combine(directory, "function-words.json");

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE entries (
                word TEXT PRIMARY KEY,
                word_lower TEXT NOT NULL,
                phonetic TEXT,
                translation TEXT NOT NULL,
                bnc INTEGER,
                frq INTEGER,
                collins INTEGER,
                oxford INTEGER,
                exchange TEXT
            );
            CREATE INDEX idx_entries_lower ON entries(word_lower);
            CREATE TABLE forms (form TEXT PRIMARY KEY, word TEXT NOT NULL);
            CREATE INDEX idx_forms_word ON forms(word);
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

            INSERT INTO entries(word, word_lower, phonetic, translation)
            VALUES
                ('run', 'run', '/rʌn/', 'v. 跑, 运行\nn. 奔跑, 一段路程'),
                ('break the ice', 'break the ice', NULL, '打破僵局, 打破沉默'),
                ('quick', 'quick', NULL, 'adj. 快的, 迅速的'),
                ('brown', 'brown', NULL, 'adj. 棕色的'),
                ('fox', 'fox', NULL, 'n. 狐狸'),
                ('jump', 'jump', NULL, 'v. 跳, 跳跃'),
                ('dog', 'dog', NULL, 'n. 狗'),
                ('lazy', 'lazy', NULL, 'adj. 懒惰的'),
                ('go', 'go', NULL, 'v. 去, 走'),
                ('be', 'be', NULL, 'v. 是, 存在');
            INSERT INTO forms(form, word)
            VALUES
                ('running', 'run'),
                ('runs', 'run'),
                ('ran', 'run'),
                ('jumps', 'jump'),
                ('went', 'go'),
                ('was', 'be'),
                ('were', 'be');
            """;
        await command.ExecuteNonQueryAsync();

        await File.WriteAllTextAsync(
            overridesPath,
            """
            {
              "the": [{ "pos": "art.", "glosses": ["这", "该"] }],
              "run": [{ "pos": "v.", "glosses": ["跑", "运行"] }]
            }
            """);

        return new TestDictionaryFixture(directory);
    }

    public string DatabasePath => Path.Combine(_directory, "test.db");

    public string OverridesPath => Path.Combine(_directory, "function-words.json");

    public DictionaryService CreateService() => new(DatabasePath, OverridesPath);

    public LiteralGlosser CreateGlosser() => new(CreateService());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
