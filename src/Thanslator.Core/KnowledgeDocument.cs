using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Thanslator.Core;

public static class KnowledgeDocument
{
    public const int MaxTextLength = 30000;
    public static string Read(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new ArgumentException("文件不能超过 2 MB。");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        string text;
        if (extension is ".txt" or ".md") text = File.ReadAllText(path, new UTF8Encoding(false, true));
        else if (extension is ".docx" or ".xlsx")
        {
            using var archive = ZipFile.OpenRead(path);
            var entries = archive.Entries.Where(e => extension == ".docx" ? e.FullName == "word/document.xml" :
                e.FullName == "xl/sharedStrings.xml" || e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToArray();
            if (entries.Length == 0 || entries.Sum(e => e.Length) > 8 * 1024 * 1024)
                throw new ArgumentException("文档格式无效或解压内容过大。");
            var builder = new StringBuilder();
            if (extension == ".docx")
            {
                XNamespace ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                var doc = Load(entries.Single());
                foreach (var paragraph in doc.Descendants(ns + "p"))
                    Append(builder, string.Concat(paragraph.Descendants(ns + "t").Select(t => t.Value)));
            }
            else
            {
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var stringsEntry = entries.FirstOrDefault(e => e.FullName == "xl/sharedStrings.xml");
                var strings = stringsEntry is null ? [] : Load(stringsEntry).Descendants(ns + "si")
                    .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToArray();
                foreach (var sheet in entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)).OrderBy(e => e.FullName, StringComparer.Ordinal))
                {
                    Append(builder, $"[{sheet.FullName}]");
                    foreach (var row in Load(sheet).Descendants(ns + "row"))
                    {
                        var values = row.Elements(ns + "c").Select(cell =>
                        {
                            var value = cell.Element(ns + "v")?.Value ?? "";
                            return (string?)cell.Attribute("t") switch
                            {
                                "s" => int.TryParse(value, out var index) && index >= 0 && index < strings.Length ? strings[index] :
                                    throw new ArgumentException("Excel 共享字符串索引无效。"),
                                "inlineStr" => string.Concat(cell.Descendants(ns + "t").Select(t => t.Value)),
                                _ => value
                            };
                        });
                        Append(builder, string.Join("\t", values));
                    }
                }
            }
            text = builder.ToString();
        }
        else throw new ArgumentException("智能整理支持 TXT、Markdown、DOCX 和 XLSX，暂不支持扫描件。");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("没有可读取的正文；扫描件和图片需先转为文本。");
        if (text.Length > MaxTextLength) throw new ArgumentException("正文超过 30000 字符，请拆分文件后导入；不会自动截断。");
        return text;
    }
    private static void Append(StringBuilder builder, string text)
    {
        if (builder.Length + text.Length + 1 > MaxTextLength) throw new ArgumentException("正文超过 30000 字符，请拆分文件后导入。");
        builder.AppendLine(text);
    }
    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
        return XDocument.Load(reader);
    }
}

public static class KnowledgeConversion
{
    private static string Fold(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public static TermImportPreview Parse(string json, string sourceText, string sourceName,
        IEnumerable<KnowledgeTerm> existing)
    {
        if (json.Length > 1024 * 1024) throw new ArgumentException("转换结果过大。");
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("terms");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 500)
            throw new ArgumentException("单次最多整理 500 条术语，请拆分资料。");
        var names = new HashSet<string>(existing.Select(t => KnowledgeRules.Normalize(t.Term)), StringComparer.Ordinal);
        var terms = new List<KnowledgeTerm>();
        var errors = new List<TermImportError>();
        var source = Fold(sourceText);
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            index++;
            var term = row.GetProperty("term").GetString() ?? "";
            var translation = row.GetProperty("translation").GetString() ?? "";
            var evidence = row.GetProperty("source").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(evidence) || evidence.Length > 700 ||
                !source.Contains(Fold(evidence), StringComparison.Ordinal) || string.IsNullOrWhiteSpace(term) ||
                string.IsNullOrWhiteSpace(translation) || !Fold(evidence).Contains(Fold(term), StringComparison.Ordinal) ||
                !Fold(evidence).Contains(Fold(translation), StringComparison.Ordinal))
            { errors.Add(new(index, "原词、译法或对应原文片段无法在资料中核对，未补写，请检查。")); continue; }
            var rule = new KnowledgeTerm(Guid.NewGuid().ToString("N"), term, translation,
                $"来源：{Path.GetFileName(sourceName)}\n原文：{evidence}");
            try { KnowledgeRules.ValidateTerm(rule); }
            catch (ArgumentException ex) { errors.Add(new(index, ex.Message)); continue; }
            if (!names.Add(KnowledgeRules.Normalize(term))) { errors.Add(new(index, "与现有条目或本次结果重复，请处理后导入。")); continue; }
            if (names.Count > KnowledgeRules.MaxTerms) { errors.Add(new(index, "每库最多 5000 条术语。")); break; }
            terms.Add(rule);
        }
        if (doc.RootElement.TryGetProperty("unresolved", out var unresolved))
        {
            if (unresolved.ValueKind != JsonValueKind.Array) throw new ArgumentException("待处理项格式无效。");
            if (unresolved.GetArrayLength() > 500) throw new ArgumentException("待处理项过多，请拆分资料。");
            foreach (var item in unresolved.EnumerateArray())
            {
                var fragment = item.GetString() ?? "";
                errors.Add(new(0, "待处理：缺少译法或对应关系不明确。" + (fragment.Length > 200 ? fragment[..200] : fragment)));
            }
        }
        if (terms.Count == 0 && errors.Count == 0) errors.Add(new(0, "未找到可确认的已有术语对照；不会生成缺失译法。"));
        return new(terms, errors);
    }
}
