using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Thanslator.Core;

public sealed record TranslationPreference(string Id,string Source,string Target,string Scope,string Rule,bool Enabled=true);

public static class TranslationPreferences
{
    public static IReadOnlyList<TranslationPreference> Selected(JsonObject state,string source,string target) =>
        Applicable(state,target).Where(rule=>rule.Source=="auto" || rule.Source==source).ToArray();

    public static IReadOnlyList<TranslationPreference> Applicable(JsonObject state,string target) =>
        state["enabled"]?.GetValue<bool>()==false ? [] :
        state["rules"]!.AsArray().OfType<JsonObject>().Select(Read)
        .Where(rule=>rule.Enabled && rule.Target==target &&
            (rule.Scope.Length==0 || rule.Scope==MoreSettingsKnowledge.Text(state,"scope"))).ToArray();

    public static TranslationPreference Read(JsonObject row) => new(MoreSettingsKnowledge.Text(row,"id"),
        MoreSettingsKnowledge.Text(row,"source"),MoreSettingsKnowledge.Text(row,"target"),MoreSettingsKnowledge.Text(row,"scope"),
        MoreSettingsKnowledge.Text(row,"rule"),row["enabled"]?.GetValue<bool>()!=false);

    public static void Validate(JsonObject state)
    {
        if(state["rules"] is not JsonArray rules || rules.Count>100 || state["candidates"] is not JsonArray candidates || candidates.Count>300 ||
            MoreSettingsKnowledge.Text(state,"scope").Length>80 || state["collect"]?.GetValue<bool>() is null)
            throw new ArgumentException("翻译偏好配置无效。");
        var ids=new HashSet<string>();
        foreach(var item in rules)
        {
            var rule=Read(item!.AsObject());
            if(!ids.Add(rule.Id) || rule.Id.Length is <1 or >100 || rule.Rule.Trim().Length is <1 or >500 || rule.Scope.Length>80 ||
                (rule.Source!="auto" && !MoreSettingsKnowledge.ValidLanguage(rule.Source)) || !MoreSettingsKnowledge.ValidLanguage(rule.Target) || rule.Source==rule.Target)
                throw new ArgumentException("偏好需有有效的语言方向与不超过 500 字的规则。");
        }
    }

    public static IReadOnlyList<string> DetectEdits(string source,string before,string after)
    {
        if(before==after)return [];
        var result=new List<string>();
        var sourceBreaks=Regex.Matches(source,@"\r?\n").Count;
        if(sourceBreaks>=1 && Regex.Matches(before,@"\r?\n").Count!=sourceBreaks && Regex.Matches(after,@"\r?\n").Count==sourceBreaks)
            result.Add("保留原文的段落分隔");
        var acronyms=Regex.Matches(source,@"\b[A-Z][A-Z0-9]{1,9}\b").Select(match=>match.Value).Distinct();
        if(acronyms.Any(word=>!before.Contains(word,StringComparison.Ordinal) && after.Contains(word,StringComparison.Ordinal)))
            result.Add("专业缩写首次出现时保留原文缩写");
        var number=Regex.Match(source,@"^\s*(\d+[.)、])\s*");
        if(number.Success && !before.TrimStart().StartsWith(number.Groups[1].Value,StringComparison.Ordinal) &&
            after.TrimStart().StartsWith(number.Groups[1].Value,StringComparison.Ordinal))result.Add("保留原文的数字编号格式");
        return result;
    }
}
