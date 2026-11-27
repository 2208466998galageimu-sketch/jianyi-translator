using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Thanslator.Translation;

internal static class LocalMtMatrix
{
    public static async Task RunAsync(string host, string directory, string output)
    {
        var examples = new Dictionary<string,string> {
            ["en"] = "Please save the file before closing the application.",
            ["zh-Hans"] = "请在关闭应用程序之前保存文件。",
            ["ja"] = "アプリケーションを閉じる前にファイルを保存してください。",
            ["ko"] = "응용 프로그램을 닫기 전에 파일을 저장하세요.",
            ["fr"] = "Veuillez enregistrer le fichier avant de fermer l’application.",
            ["de"] = "Bitte speichern Sie die Datei, bevor Sie die Anwendung schließen.",
            ["es"] = "Guarde el archivo antes de cerrar la aplicación."
        };
        var rows = new List<object>();
        var errors = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        foreach (var from in examples.Keys)
        foreach (var to in examples.Keys.Where(to => to != from))
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var options = LanguagePack.CreateOptions(host, directory, from, to, "Jianyi.Matrix." + Guid.NewGuid().ToString("N"));
                await using var provider = new LocalMtProvider(options);
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var result = await provider.TranslateDocumentAsync(examples[from], cancellationToken: cancellation.Token);
                if (result.FailedCount != 0) throw new Exception("The translation failed validation: " + result.Text);
                rows.Add(new { source = from, target = to, text = result.Text, elapsedMs = watch.ElapsedMilliseconds, ok = true });
            }
            catch (Exception ex)
            {
                errors++;
                rows.Add(new { source = from, target = to, error = ex.Message, elapsedMs = watch.ElapsedMilliseconds, ok = false });
            }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { errors, completed = rows.Count, rows }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (errors > 0) throw new Exception($"{errors} translation directions failed. See {output}");
    }
}
