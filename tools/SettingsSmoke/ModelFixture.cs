using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class ModelFixture : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string? _models;
    private readonly CancellationTokenSource _stop = new();
    public string Address { get; }
    public ModelFixture(int port = 0, string? models = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, port); _models = models;
        _listener.Start();
        Address = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
        _ = Accept();
    }
    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Serve(client);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = new List<byte>();
                while (header.Count < 65536)
                {
                    var next = new byte[1];
                    if (await stream.ReadAsync(next, _stop.Token) == 0) return;
                    header.Add(next[0]);
                    if (header.Count > 3 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var headers = Encoding.ASCII.GetString(header.ToArray());
                var lengthLine = headers.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                using var body = new MemoryStream();
                if (lengthLine is not null)
                {
                    var count = int.Parse(lengthLine.Split(':')[1]);
                    if (count > 1024 * 1024) return;
                    var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, _stop.Token); body.Write(bytes);
                }
                else if (headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
                {
                    while (true)
                    {
                        var line = new List<byte>();
                        while (true)
                        {
                            var next = new byte[1]; await stream.ReadExactlyAsync(next, _stop.Token); line.Add(next[0]);
                            if (line.Count > 1 && line[^2] == 13 && line[^1] == 10) break;
                        }
                        var count = Convert.ToInt32(Encoding.ASCII.GetString(line.ToArray()).Trim(), 16);
                        if (count > 1024 * 1024 || body.Length + count > 1024 * 1024) return;
                        if (count == 0) { var end = new byte[2]; await stream.ReadExactlyAsync(end, _stop.Token); break; }
                        var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, _stop.Token); body.Write(bytes);
                        var crlf = new byte[2]; await stream.ReadExactlyAsync(crlf, _stop.Token);
                    }
                }
                string content;
                if(headers.StartsWith("GET /v1/models"))
                    content="""{"data":[{"id":"fixture-translation"},{"id":"fixture-assistant"}]}""";
                else if (headers.StartsWith("GET /api/tags"))
                {
                    var models = new List<string>();
                    if (_models is null || File.Exists(Path.Combine(_models, "fixture-model.txt"))) models.Add("qwen2.5:1.5b");
                    if (_models is not null && File.Exists(Path.Combine(_models, "fixture-unusable.txt"))) models.Add("qwen2.5:3b");
                    content = JsonSerializer.Serialize(new { models = models.Select(name => new { name, size = 1 }) });
                }
                else if (headers.StartsWith("POST /api/pull"))
                {
                    if (_models is not null)
                    {
                        Directory.CreateDirectory(_models);
                        var requested = JsonNode.Parse(body.ToArray())?["model"]?.GetValue<string>();
                        var marker = requested == "qwen2.5:3b" ? "fixture-unusable.txt" : "fixture-model.txt";
                        File.WriteAllText(Path.Combine(_models, marker), "Protocol fixture, not model weights");
                    }
                    content = """{"status":"success"}""" + "\n";
                }
                else
                {
                    var response = "连接已就绪。";
                    var data = JsonNode.Parse(body.ToArray());
                    var text = data?["messages"]?.AsArray().LastOrDefault()?["content"]?.GetValue<string>();
                    JsonNode? task = null; try { task = JsonNode.Parse(text ?? ""); } catch (JsonException) { }
                    if(data?["messages"]?[0]?["content"]?.GetValue<string>()?.Contains("Check JSON")==true || text=="Check JSON output capability.")
                        response="""{"ready":true}""";
                    else if(task?["source_units"] is JsonArray units)
                        response=JsonSerializer.Serialize(new {source_language="en",translations=units.Select(unit=>new {id=unit!["id"]!.GetValue<int>(),text="连接已就绪。"})});
                    else if (task?["task"]?.GetValue<string>() == "generate_terminology")
                        response = """{"entries":[{"source":"sensor","target":"传感器","source_lang":"en","target_lang":"zh-Hans","note":"测试候选"},{"source":"传感器","target":"sensor","source_lang":"zh-Hans","target_lang":"en"}]}""";
                    else if (task?["task"]?.GetValue<string>() == "detect_languages")
                        response = JsonSerializer.Serialize(new { entries = task["entries"]!.AsArray().Select(item => new {
                            index = item!["index"]!.GetValue<int>(), source_lang = "en", target_lang = "zh-Hans"
                        }) });
                    content = _models is not null && data?["model"]?.GetValue<string>() == "qwen2.5:3b"
                        ? """{"choices":[]}"""
                        : JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = response } } } });
                }
                var payload = Encoding.UTF8.GetBytes(content);
                var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader, _stop.Token);
                await stream.WriteAsync(payload, _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
        }
    }
    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}


