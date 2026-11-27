using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class PrimaryMeaningTableTests
{
    [Fact]
    public void Load_ReadsWordToGlossMapCaseInsensitively()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "thanslator-primary-tests",
            Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "run": "跑", "Windows": "窗户" }""");

        try
        {
            var table = PrimaryMeaningTable.Load(path);

            Assert.True(table.TryGet("run", out var run));
            Assert.Equal("跑", run);
            Assert.True(table.TryGet("windows", out var windows));
            Assert.Equal("窗户", windows);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
