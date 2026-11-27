using System.Text.Json.Nodes;

namespace Thanslator.Core;

public static class KnowledgeChoices
{
    public static IReadOnlyList<KnowledgeHit> Candidates(KnowledgeMatches matches, string term) =>
        (matches.Candidates ?? matches.Hits.Concat(matches.Conflicts.Select(conflict=>conflict.Alternative)).ToArray())
        .Where(hit=>KnowledgeRules.Normalize(hit.Rule.Term)==KnowledgeRules.Normalize(term)).ToArray();

    public static KnowledgeMatches ApplyPreferred(KnowledgeMatches matches, JsonNode? combo, string source, string target)
    {
        if (combo?["preferred"] is not JsonArray choices) return matches;
        var hits = matches.Hits.Select(hit => {
            var choice = choices.FirstOrDefault(item=>MoreSettingsKnowledge.Text(item,"direction")==source+">"+target &&
                MoreSettingsKnowledge.Text(item,"term")==KnowledgeRules.Normalize(hit.Rule.Term));
            return Candidates(matches,hit.Rule.Term).FirstOrDefault(candidate=>candidate.LibraryId==MoreSettingsKnowledge.Text(choice,"libraryId") &&
                candidate.Rule.Id==MoreSettingsKnowledge.Text(choice,"ruleId")) ?? hit;
        }).ToArray();
        return matches with { Hits=hits };
    }

    public static void SavePreferred(JsonObject document,string presetId,string source,string target,KnowledgeHit choice)
    {
        var combo=document["presets"]!.AsArray().FirstOrDefault(item=>!MoreSettingsKnowledge.Deleted(item!) && MoreSettingsKnowledge.Text(item,"id")==presetId)
            ?? throw new ArgumentException("原组合已删除，未保存首选译法。");
        var current=KnowledgeRules.Match(choice.Rule.Term,source,target,MoreSettingsKnowledge.SelectedLibraries(
            WithActive(document,presetId),source,target));
        if (!Candidates(current,choice.Rule.Term).Any(hit=>hit.LibraryId==choice.LibraryId && hit.Rule.Id==choice.Rule.Id && hit.Rule.Translation==choice.Rule.Translation))
            throw new ArgumentException("词条或来源已改变，请重新翻译后再保存。");
        var list=combo["preferred"] as JsonArray ?? new JsonArray();
        if (combo["preferred"] is null) combo["preferred"]=list;
        var term=KnowledgeRules.Normalize(choice.Rule.Term);
        var previous=list.FirstOrDefault(item=>MoreSettingsKnowledge.Text(item,"direction")==source+">"+target && MoreSettingsKnowledge.Text(item,"term")==term);
        if(previous is not null)list.Remove(previous);
        list.Add(new JsonObject { ["direction"]=source+">"+target,["term"]=term,["libraryId"]=choice.LibraryId,["ruleId"]=choice.Rule.Id });
    }

    private static JsonObject WithActive(JsonObject document,string preset)
    { var copy=(JsonObject)document.DeepClone();copy["active"]=preset;return copy; }
}
