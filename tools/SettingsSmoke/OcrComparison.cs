using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Thanslator.Capture;

internal static class OcrComparison
{
    private static byte[] Image(string[] lines, bool chinese)
    {
        using var bitmap = new Bitmap(920, 180);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(chinese ? "Microsoft YaHei UI" : "Segoe UI", 19, FontStyle.Regular, GraphicsUnit.Pixel);
        for (var index = 0; index < lines.Length; index++) graphics.DrawString(lines[index], font, Brushes.Black, 16, 12 + index * 44);
        using var buffer = new MemoryStream(); bitmap.Save(buffer, ImageFormat.Png); return buffer.ToArray();
    }

    public static async Task RunAsync(string baseline, string compact, string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("PYTHONPATH", null);
        Environment.SetEnvironmentVariable("PYTHONHOME", null);
        using var original = new OcrHostClient(baseline);
        using var optimized = new OcrHostClient(compact);
        var fixtures = new[] {
            Image(["Restart the app after installing the update.", "Sensor fusion improves the measurement accuracy."], false),
            Image(["传感器融合与智能感知工程", "安装完成后重新启动应用"], true),
            Image(["function translate(source, target)", "return model.predict(input_text)", "model_version = 12345"], false)
        };
        var beforeTimes = new List<double>(); var afterTimes = new List<double>();
        var evidence = new List<object>();
        for (var fixture = 0; fixture < fixtures.Length; fixture++)
        {
            var bytes = fixtures[fixture];
            File.WriteAllBytes(Path.Combine(output, $"fixture-{fixture}.png"), bytes);
            for (var iteration = 0; iteration < 5; iteration++)
            {
                var watch = Stopwatch.StartNew();
                var before = await original.RecognizeAsync(bytes, 920, 180, CancellationToken.None);
                var beforeMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                var after = await optimized.RecognizeAsync(bytes, 920, 180, CancellationToken.None);
                var afterMs = watch.Elapsed.TotalMilliseconds;
                if (before.Count == 0 || before.Count != after.Count ||
                    !before.Zip(after).All(pair => pair.First.Text == pair.Second.Text &&
                        pair.First.Bounds == pair.Second.Bounds &&
                        Math.Abs(pair.First.Confidence - pair.Second.Confidence) < 0.00001))
                    throw new InvalidOperationException($"OCR fixture {fixture} differs after compaction.");
                if (iteration > 0) { beforeTimes.Add(beforeMs); afterTimes.Add(afterMs); }
                else evidence.Add(new { fixture, lines = before.Count, text = before.Select(line => line.Text), firstPassBeforeMs = beforeMs, firstPassAfterMs = afterMs });
            }
        }
        double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
        var originalProcess = (Process)typeof(OcrHostClient).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(original)!;
        var compactProcess = (Process)typeof(OcrHostClient).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(optimized)!;
        var report = new { baseline, compact, equal = true, fixtureCount = fixtures.Length, runsPerFixture = 5,
            beforeMedianMs = Median(beforeTimes), afterMedianMs = Median(afterTimes),
            beforePeakBytes = originalProcess.PeakWorkingSet64, afterPeakBytes = compactProcess.PeakWorkingSet64, evidence };
        File.WriteAllText(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}

