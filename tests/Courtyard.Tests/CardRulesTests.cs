using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Courtyard.Tests;

/// <summary>The leaves preview's cards are cooked with the courtyard's own rules, so what it shows is what the courtyard gets.</summary>
public sealed class CardRulesTests
{
    private static string Models([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "examples", "Courtyard", "content", "models"));
    }

    [Fact]
    public void Every_graph_card_has_the_same_rule_as_in_the_courtyard()
    {
        var courtyard = Rules("courtyard.glb.cook.json");
        var cards = Rules("cards.glb.cook.json").Where(rule => rule.Key.StartsWith("graph_", StringComparison.Ordinal)).ToList();

        // Ivy in two sizes, three grasses, a cobweb and three dead leaves.
        Assert.Equal(9, cards.Count);
        foreach (var (match, rule) in cards)
        {
            Assert.True(courtyard.TryGetValue(match, out var theirs), $"The courtyard has no rule for {match}.");
            Assert.Equal(theirs, rule);
        }
    }

    [Fact]
    public void No_card_rule_matches_another_cards_material()
    {
        // A rule applies to every material whose name contains its match, and the first one wins, so a match that
        // is part of another's name would take that material too.
        var matches = Rules("cards.glb.cook.json").Keys.Concat(Rules("courtyard.glb.cook.json").Keys).Distinct().ToList();
        foreach (string card in Rules("cards.glb.cook.json").Keys)
        {
            var others = matches.Where(match => match != card && card.Contains(match, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(others);
        }
    }

    // Each rule's JSON, without spaces, by its match.
    private static Dictionary<string, string> Rules(string sidecar)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Models(), sidecar)))!;
        return json["materials"]!.AsArray().ToDictionary(rule => (string)rule!["match"]!, rule => rule!.ToJsonString());
    }
}
