using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal static class RelationshipCatalog
{
    public static readonly IReadOnlyList<RelationshipDefinition> Definitions =
    [
        Define("biological-parent", "Biological parent", "biological-child",
            "parent", "biological mother", "biological father", "mother", "father", "birth parent"),
        Define("biological-child", "Biological child", "biological-parent",
            "child", "son", "daughter", "offspring"),
        Define("biological-sibling", "Biological sibling", "biological-sibling",
            "sibling", "brother", "sister"),
        Define("biological-full-sibling", "Full biological sibling", "biological-full-sibling",
            "full sibling", "full brother", "full sister"),
        Define("biological-half-sibling", "Half biological sibling", "biological-half-sibling",
            "half sibling", "half brother", "half sister"),
        Define("biological-twin", "Biological twin", "biological-twin",
            "twin", "identical twin", "fraternal twin"),
        Define("biological-grandparent", "Biological grandparent", "biological-grandchild",
            "grandparent", "grandmother", "grandfather"),
        Define("biological-grandchild", "Biological grandchild", "biological-grandparent",
            "grandchild", "grandson", "granddaughter"),
        Define("biological-great-grandparent", "Biological great-grandparent", "biological-great-grandchild",
            "great grandparent", "great-grandmother", "great-grandfather"),
        Define("biological-great-grandchild", "Biological great-grandchild", "biological-great-grandparent",
            "great grandchild", "great-grandson", "great-granddaughter"),
        Define("biological-pibling", "Biological aunt/uncle (pibling)", "biological-nibling",
            "aunt", "uncle", "pibling", "parent's sibling"),
        Define("biological-nibling", "Biological niece/nephew (nibling)", "biological-pibling",
            "niece", "nephew", "nibling", "sibling's child"),
        Define("biological-cousin", "Biological cousin", "biological-cousin",
            "cousin", "first cousin"),
        Define("biological-ancestor", "Biological ancestor", "biological-descendant",
            "ancestor", requestable: false),
        Define("biological-descendant", "Biological descendant", "biological-ancestor",
            "descendant", requestable: false)
    ];

    // Every rule is a path A -> B -> ... -> Z. Adding or removing direct facts causes
    // the whole inferred graph to be recalculated, so derived results never become stale.
    public static readonly IReadOnlyList<RelationshipInferenceRule> InferenceRules =
    [
        Rule("twin-is-sibling", ["biological-twin"], "biological-sibling", "twins are biological siblings"),
        Rule("full-sibling-is-sibling", ["biological-full-sibling"], "biological-sibling", "full siblings are biological siblings"),
        Rule("half-sibling-is-sibling", ["biological-half-sibling"], "biological-sibling", "half siblings are biological siblings"),
        Rule("shared-parent", ["biological-child", "biological-parent"], "biological-sibling", "both characters share a biological parent"),
        Rule("parent-of-parent", ["biological-parent", "biological-parent"], "biological-grandparent", "parent of a biological parent"),
        Rule("three-parent-generations", ["biological-parent", "biological-parent", "biological-parent"], "biological-great-grandparent", "three biological parent generations"),
        Rule("sibling-of-parent", ["biological-sibling", "biological-parent"], "biological-pibling", "biological sibling of a parent"),
        Rule("children-of-siblings", ["biological-child", "biological-sibling", "biological-parent"], "biological-cousin", "their biological parents are siblings"),
        Rule("grandparent-is-ancestor", ["biological-grandparent"], "biological-ancestor", "a biological grandparent is an ancestor"),
        Rule("great-grandparent-is-ancestor", ["biological-great-grandparent"], "biological-ancestor", "a biological great-grandparent is an ancestor"),
        Rule("ancestor-through-parent", ["biological-ancestor", "biological-parent"], "biological-ancestor", "ancestor through another biological generation"),
        Rule("parent-of-ancestor", ["biological-parent", "biological-ancestor"], "biological-ancestor", "parent of a biological ancestor")
    ];

    private static readonly IReadOnlyDictionary<string, RelationshipDefinition> ById =
        Definitions.ToDictionary(definition => definition.Id, StringComparer.OrdinalIgnoreCase);

    public static RelationshipDefinition? Get(string id) =>
        ById.TryGetValue(id, out var definition) ? definition : null;

    public static RelationshipDefinition? Resolve(string value)
    {
        var normalized = Normalize(value);
        return Definitions.FirstOrDefault(definition =>
            Normalize(definition.Id) == normalized ||
            Normalize(definition.DisplayName) == normalized ||
            definition.Aliases.Any(alias => Normalize(alias) == normalized));
    }

    public static bool Equivalent(
        RelationshipRecord relationship,
        Guid sourceCharacterId,
        Guid targetCharacterId,
        string typeId)
    {
        if (relationship.SourceCharacterId == sourceCharacterId &&
            relationship.TargetCharacterId == targetCharacterId &&
            relationship.TypeId.Equals(typeId, StringComparison.OrdinalIgnoreCase))
            return true;

        var inverse = Get(typeId)?.InverseId;
        return inverse is not null &&
               relationship.SourceCharacterId == targetCharacterId &&
               relationship.TargetCharacterId == sourceCharacterId &&
               relationship.TypeId.Equals(inverse, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesSearch(RelationshipDefinition definition, string typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return true;
        return definition.DisplayName.Contains(typed, StringComparison.OrdinalIgnoreCase) ||
               definition.Id.Contains(typed, StringComparison.OrdinalIgnoreCase) ||
               definition.Aliases.Any(alias => alias.Contains(typed, StringComparison.OrdinalIgnoreCase));
    }

    private static RelationshipDefinition Define(
        string id,
        string displayName,
        string inverseId,
        string firstAlias,
        params string[] aliases) =>
        Define(id, displayName, inverseId, firstAlias, true, aliases);

    private static RelationshipDefinition Define(
        string id,
        string displayName,
        string inverseId,
        string firstAlias,
        bool requestable,
        params string[] aliases) =>
        new(id, displayName, inverseId, "Biological", requestable, [firstAlias, .. aliases]);

    private static RelationshipInferenceRule Rule(
        string id,
        IReadOnlyList<string> path,
        string result,
        string explanation) => new(id, path, result, explanation);

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
