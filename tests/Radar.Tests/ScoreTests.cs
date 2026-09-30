using System.Text.Json;
using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class ScoreTests
{
    [TestMethod]
    public void ScoresFoundPartialMissedAndUnmatchedWithRuleSemantics()
    {
        using var findings = JsonDocument.Parse("""
            {"Findings":[
              {"category":"SUNSET","path":"Campaign.Old","file":"src/Api.cs","line":9,"message":"Removed soon","evidence":["Release NOTE"]},
              {"category":"DEPRECATED","path":"Widget.Legacy","file":"src/Other.cs","line":3,"message":"Keep"},
              {"category":"UNKNOWN","path":"unrelated","file":"src/Third.cs","message":"review"}
            ]}
            """);
        using var key = JsonDocument.Parse("""
            {"items":[
              {"id":"K1","counted":true,"description":"Found","found":{"any":["absent","RELEASE note"],"fileAny":["none","API.CS"],"categoryIn":["DEPRECATED","SUNSET"]}},
              {"id":"K2","counted":true,"description":"Partial","found":{"any":["missing"]},"partial":{"any":["legacy"],"fileAny":["OTHER"]}},
              {"id":"K3","counted":true,"description":"Missed","found":{}},
              {"id":"K4","counted":false,"description":"AND failure","found":{"any":["campaign"],"fileAny":["Other.cs"]}},
              {"id":"K5","counted":false,"description":"Category exact","found":{"categoryIn":["sunset"]}}
            ]}
            """);
        string result = ScoreCommand.Evaluate(findings.RootElement, key.RootElement);
        StringAssert.Contains(result, "| K1 | true | found | src/Api.cs:9 |");
        StringAssert.Contains(result, "| K2 | true | partial | src/Other.cs:3 |");
        StringAssert.Contains(result, "| K3 | true | missed |  |");
        StringAssert.Contains(result, "| K4 | false | missed |  |");
        StringAssert.Contains(result, "| K5 | false | missed |  |");
        StringAssert.Contains(result, "found 1 + partial 1 of 3 = 50%");
        StringAssert.Contains(result, "Findings: total 3; matched a key item: 2; unmatched: 1");
        StringAssert.Contains(result, "- UNKNOWN: 1");
    }

    [TestMethod]
    public void OneFindingMatchesAtMostOneKeyItem()
    {
        using var findings = JsonDocument.Parse("""
            {"findings":[{"category":"SUNSET","path":"campaign.old","file":"A.cs","line":1,"message":"gone"}]}
            """);
        using var key = JsonDocument.Parse("""
            {"items":[
              {"id":"K1","counted":true,"description":"a","found":{"any":["campaign.old"]}},
              {"id":"K2","counted":true,"description":"b","found":{"fileAny":["A.cs"]}}
            ]}
            """);
        string result = ScoreCommand.Evaluate(findings.RootElement, key.RootElement);
        StringAssert.Contains(result, "| K2 | true | missed |  |");
        StringAssert.Contains(result, "found 1 + partial 0 of 2 = 50%");
    }
}
