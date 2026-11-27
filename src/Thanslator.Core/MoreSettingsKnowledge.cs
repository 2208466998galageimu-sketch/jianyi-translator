using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Thanslator.Core;

public static class MoreSettingsKnowledge
{
    public static JsonObject Migrate(IReadOnlyList<KnowledgeLibrary> libraries, string[] selected)
    {
        var items = new JsonArray();
        foreach (var library in libraries)
        {
            var terms = new JsonArray();
            foreach (var term in library.Terms)
                terms.Add(new JsonObject {
                    ["id"] = term.Id, ["source"] = term.Term, ["target"] = term.Translation,
                    ["note"] = term.Note, ["enabled"] = term.Enabled,
                    ["direction"] = library.SourceLang + ">" + library.TargetLang
                });
            items.Add(new JsonObject {
                ["id"] = library.Id, ["name"] = library.Name, ["domain"] = library.Domain,
                ["kind"] = "pair", ["direction"] = library.SourceLang + ">" + library.TargetLang,
                ["hits"] = 0, ["terms"] = terms
            });
        }
        var ids = new JsonArray();
        foreach (var id in selected.Where(id => libraries.Any(library => library.Id == id)).Distinct())
            ids.Add(id);
        return new JsonObject {
            ["libraries"] = items, ["presets"] = new JsonArray(new JsonObject {
                ["id"] = "default", ["name"] = "默认组合", ["mode"] = "manual", ["ids"] = ids
            }), ["active"] = "default", ["trashSchema"] = 1
        };
    }

    public static string Text(JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";
    public static bool Deleted(JsonNode node) => !string.IsNullOrEmpty(Text(node, "deletedAt"));
    public static string Direction(JsonNode term, JsonNode library) =>
        Text(term, "direction") is { Length: > 0 } direction ? direction : Text(library, "direction");

    public static void Validate(JsonObject document)
    {
        if (document["libraries"] is not JsonArray libraries || document["presets"] is not JsonArray presets ||
            libraries.Count > 500 || presets.Count > 500) throw new ArgumentException("知识库或组合格式无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in libraries)
        {
            if (library is not JsonObject || !ids.Add(Text(library, "id")) || Text(library, "id").Length is < 1 or > 100 ||
                Text(library, "name").Trim().Length is < 1 or > 80 ||
                Text(library, "kind") is not ("mixed" or "pair") || library["terms"] is not JsonArray terms)
                throw new ArgumentException("知识库名称或标识无效。");
            ValidateOptionalText(library, "domain", "direction", "deletedAt");
            if (library is JsonObject libraryObject && !OptionalBoolean(libraryObject, "personal"))
                throw new ArgumentException("知识库的个人库标记必须为布尔值。");
            if (library["versions"] is not null and not JsonArray)
                throw new ArgumentException("版本记录无效。");
            ValidateTerms(library, terms);
            if (library["versions"] is JsonArray versions)
            {
                var versionIds = new HashSet<int>();
                foreach (var version in versions)
                {
                    if (version?["number"] is null || !versionIds.Add(version["number"]!.GetValue<int>()) ||
                        version["terms"] is not JsonArray oldTerms) throw new ArgumentException("版本记录无效。");
                    ValidateTerms(library, oldTerms);
                }
            }
        }
        var comboIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var combo in presets)
        {
            if (combo is null || !comboIds.Add(Text(combo, "id")) ||
                Text(combo, "name").Trim().Length is < 1 or > 80 ||
                Text(combo, "mode") is not ("manual" or "frequency") || combo["ids"] is not JsonArray selected ||
                selected.Select(id => id!.GetValue<string>()).Distinct().Count() != selected.Count ||
                selected.Any(id => !ids.Contains(id!.GetValue<string>())))
                throw new ArgumentException("组合配置无效或引用了已彻底删除的知识库。");
            ValidateOptionalText(combo, "deletedAt");
        }
        var active = Text(document, "active");
        if (active.Length > 0 && !presets.Any(combo => combo is not null && !Deleted(combo) && Text(combo, "id") == active))
            throw new ArgumentException("当前组合不可用。");
    }

    private static void ValidateTerms(JsonNode library, JsonArray terms)
    {
        if (terms.Count > KnowledgeRules.MaxTerms) throw new ArgumentException("每个知识库最多 5000 条术语。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            if (term is not JsonObject entry) throw new ArgumentException("词条格式无效。");
            ValidateOptionalText(entry, "id", "source", "target", "direction", "note", "reference");
            if (!OptionalBoolean(entry, "enabled"))
                throw new ArgumentException($"知识库「{Text(library, "name")}」的词条「{Text(entry, "source")}」启用状态必须是布尔值。");
            var source = Text(term, "source");
            var target = Text(term, "target");
            var direction = Direction(term, library);
            var pair = direction.Split('>');
            if (pair.Length != 2 || pair[0] == pair[1] || pair.Any(code => !ValidLanguage(code)) ||
                source.Trim().Length is < 1 or > 200 || target.Trim().Length is < 1 or > 500 ||
                Text(term, "note").Length > 1000 || !names.Add(direction + "|" + KnowledgeRules.Normalize(source)))
                throw new ArgumentException("存在空词条、重复词条或无效翻译方向。");
            if (Text(library, "kind") == "pair")
            {
                var expected = Text(library, "direction").Split('>');
                if (expected.Length != 2 || !pair.Order().SequenceEqual(expected.Order()))
                    throw new ArgumentException("词条与知识库的语言对不符。");
            }
        }
    }

    private static bool OptionalBoolean(JsonObject item, string key) =>
        !item.ContainsKey(key) || item[key]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    private static void ValidateOptionalText(JsonNode item, params string[] keys)
    {
        if (keys.Any(key => item[key] is { } value && value.GetValueKind() != JsonValueKind.String))
            throw new ArgumentException("知识库包含类型无效的文本字段。");
    }

    public static string ExportDelimited(JsonNode library,char separator=',')
    {
        if(separator is not (',' or '\t'))throw new ArgumentException("导出分隔符无效。");
        var terms=library["terms"]!.AsArray();ValidateTerms(library,terms);
        string Quote(string text)=>"\""+text.Replace("\"","\"\"")+"\"";
        var headings=new[]{"term","translation","source_lang","target_lang","note","reference","enabled","jianyi_escaped_fields"};
        var rows=new List<string>{string.Join(separator,headings)};
        foreach(var term in terms)
        {
            var pair=Direction(term!,library).Split('>');
            var fields=new[]{Text(term,"source"),Text(term,"target"),pair[0],pair[1],Text(term,"note"),Text(term,"reference"),term!["enabled"]?.GetValue<bool>()==false?"false":"true",""};
            var escaped=new List<string>();
            foreach(var index in new[]{0,1,4,5})
            {
                var start=fields[index].TrimStart();
                if(start.Length>0&&"=+-@".Contains(start[0])){fields[index]="'"+fields[index];escaped.Add(headings[index]);}
            }
            fields[7]=string.Join(';',escaped);rows.Add(string.Join(separator,fields.Select(Quote)));
        }
        return string.Join("\r\n",rows);
    }

    public static bool ValidLanguage(string language) =>
        language.Length is >= 2 and <= 32 && language.Split('-').All(part =>
            part.Length is >= 2 and <= 8 && part.All(char.IsAsciiLetterOrDigit));

    public static IReadOnlyList<KnowledgeLibrary> SelectedLibraries(JsonObject document, string source, string target)
    {
        var libraries = document["libraries"]!.AsArray();
        var combo = document["presets"]!.AsArray().FirstOrDefault(item => item is not null &&
            !Deleted(item) && Text(item, "id") == Text(document, "active"));
        if (combo?["ids"] is not JsonArray selected) return [];
        var ordered = selected.Select(id => libraries.FirstOrDefault(library => Text(library, "id") == id!.GetValue<string>()))
            .Where(library => library is not null && !Deleted(library)).Cast<JsonNode>();
        if (Text(combo, "mode") == "frequency")
            ordered = ordered.OrderBy(library => library["personal"]?.GetValueKind() == JsonValueKind.True)
                .ThenByDescending(library => library["hits"]?.GetValue<int>() ?? 0);
        return ordered.SelectMany(library => library["terms"]!.AsArray().OfType<JsonObject>()
            .Where(term => OptionalBoolean(term, "enabled"))
            .GroupBy(term => Direction(term!, library))
            .Where(group => group.Key.Split('>') is [var from, var to] && to == target && (from == source || source == "auto" && from == "en"))
            .Select(group => new KnowledgeLibrary(Text(library, "id"), Text(library, "name"), Text(library, "domain"),
                group.Key.Split('>')[0], group.Key.Split('>')[1],
                group.Select(term => new KnowledgeTerm(
                    Text(term, "id") is { Length: > 0 } id ? id :
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key + "|" + KnowledgeRules.Normalize(Text(term, "source"))))),
                    Text(term, "source"), Text(term, "target"), Text(term, "note"),
                    term!["enabled"]?.GetValue<bool>() != false)).ToArray()))).ToArray();
    }
}
