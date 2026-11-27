using Thanslator.Core;

namespace Thanslator.Tests;

public sealed class KnowledgeExportTests
{
    [Fact]
    public void CsvPreservesLanguageAndSourceMetadata_AndEscapesSpreadsheetFormulasReversibly()
    {
        var document=MoreSettingsKnowledge.Migrate([new("id","库","","en","zh-Hans",[new("term","=SUM(1,2)","逗号,引号\"","备注")])],[]);
        var library=document["libraries"]![0]!;library["terms"]![0]!["reference"]="原资料";
        var csv=MoreSettingsKnowledge.ExportDelimited(library);
        Assert.Contains("jianyi_escaped_fields",csv);Assert.Contains("'\u003dSUM(1,2)",csv);Assert.Contains("\"term\"",csv);
        Assert.Contains("\"en\",\"zh-Hans\"",csv);Assert.Contains("原资料",csv);Assert.Contains("引号\"\"",csv);
        Assert.Throws<ArgumentException>(()=>MoreSettingsKnowledge.ExportDelimited(library,';'));
    }
}
