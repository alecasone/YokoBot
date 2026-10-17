using System.Globalization;
using System.Text;
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
            "descendant", requestable: false),
        Social("social-friend", "Friend", "Social", "friends"),
        Social("social-best-friend", "Best friend", "Social", "best friends", "bff"),
        Social("social-acquaintance", "Acquaintance", "Social", "acquaintances"),
        Social("social-confidant", "Confidant", "Social", "trusted friend"),
        Social("social-ally", "Ally", "Social", "allies"),
        Social("social-rival", "Rival", "Social", "rivals", "rivalry"),
        Social("social-enemy", "Enemy", "Social", "enemies", "nemesis"),
        Social("romantic-dating", "Dating partner", "Romantic", "dating", "boyfriend", "girlfriend", "partner"),
        Social("romantic-engaged", "Betrothed", "Romantic", "engaged", "fiance", "fiancee", "fiancé / fiancée", "betrothal"),
        Social("romantic-spouse", "Spouse", "Romantic", "married", "marriage", "husband", "wife"),
        Social("romantic-former-partner", "Former partner", "Romantic", "ex", "ex partner"),
        Pair("societal-mentor", "Mentor", "societal-student", "Societal"),
        Pair("societal-student", "Mentee", "societal-mentor", "Societal", "student"),
        Pair("societal-guardian", "Guardian", "societal-ward", "Societal", "caretaker"),
        Pair("societal-ward", "Ward", "societal-guardian", "Societal", "dependent"),
        Pair("societal-leader", "Leader", "societal-follower", "Societal"),
        Pair("societal-follower", "Follower", "societal-leader", "Societal", "follower / subject"),
        Pair("societal-employer", "Employer", "societal-employee", "Societal", "boss"),
        Pair("societal-employee", "Employee", "societal-employer", "Societal", "worker"),
        Pair("adoptive-parent", "Adoptive parent", "adoptive-child", "Adoptive", "adopted parent", "adoptive mother", "adoptive father"),
        Pair("adoptive-child", "Adoptive child", "adoptive-parent", "Adoptive", "adopted child", "adopted son", "adopted daughter"),
        Social("adoptive-sibling", "Adoptive sibling", "Adoptive", "adopted sibling"),

        // Medieval core and A-tier additions. Every new tie is explicit and approved;
        // these definitions do not extend the biological inference rules below.
        Social("social-childhood-friend", "Childhood friend", "Social", "childhood friends"),
        Social("social-close-friend", "Close friend", "Social", "close friends"),
        Social("social-former-friend", "Former friend", "Social", "ex friend"),
        Social("social-companion", "Companion", "Social", "companions"),
        Social("social-former-enemy", "Former enemy", "Social", "former enemies"),
        Social("social-neighbour", "Neighbour", "Social", "neighbor", "neighbours", "neighbors"),
        Social("social-travelling-companion", "Travelling companion", "Social", "traveling companion", "travel companion"),
        Social("social-lifelong-friend", "Lifelong friend", "Social", "lifelong friends"),
        Social("social-childhood-companion", "Childhood companion", "Social", "childhood companions"),
        Social("social-drinking-companion", "Drinking companion", "Social", "drinking companions"),
        Social("social-sworn-sibling", "Sworn sibling", "Social", "sworn brother", "sworn sister"),
        Social("social-oathbound-companion", "Oath-bound companion", "Social", "oathbound companion"),
        Social("social-trusted-companion", "Trusted companion", "Social", "trusted companions"),
        Social("social-former-companion", "Former companion", "Social", "former companions"),
        Social("social-friendly-rival", "Friendly rival", "Social", "friendly rivals"),
        Social("social-frenemy", "Frenemy", "Social", "frenemies"),
        Social("social-archrival", "Archrival", "Social", "archrivals"),
        Social("social-family-rival", "Family rival", "Social", "family rivals"),
        Social("social-chosen-family", "Chosen family", "Social", "found family"),
        Social("social-fellow-pilgrim", "Fellow pilgrim", "Social", "fellow pilgrims"),
        .. Roles("social-healer", "Healer", "social-patient", "Patient", "Social"),
        .. Roles("social-tutor", "Tutor", "social-tutored-pupil", "Tutored pupil", "Social",
            rightAliases: ["tutor's pupil"]),
        .. Roles("social-host", "Host", "social-guest", "Guest", "Social"),

        // Non-biological family roles belong to Roles & loyalties, not adoption.
        .. Roles("societal-stepparent", "Stepparent", "societal-stepchild", "Stepchild", "Societal",
            ["stepmother", "stepfather", "step parent"], ["stepson", "stepdaughter", "step child"]),
        Social("societal-stepsibling", "Stepsibling", "Societal", "stepbrother", "stepsister", "step sibling"),
        Social("societal-in-law", "In-law", "Societal", "in laws"),
        .. Roles("societal-foster-parent", "Foster parent", "societal-foster-child", "Foster child", "Societal",
            ["foster mother", "foster father"], ["foster son", "foster daughter"]),

        Social("romantic-former-spouse", "Former spouse", "Romantic", "ex spouse", "ex husband", "ex wife", "divorced"),
        Social("romantic-lover", "Lover", "Romantic", "lovers"),
        Social("romantic-former-lover", "Former lover", "Romantic", "former lovers"),
        Social("romantic-former-betrothed", "Former betrothed", "Romantic", "former fiance", "former fiancee", "broken betrothal"),
        Social("romantic-arranged-betrothed", "Arranged betrothed", "Romantic", "arranged betrothal"),
        Social("romantic-political-marriage", "Political marriage partner", "Romantic", "political spouse", "political marriage"),
        Social("romantic-consort", "Consort", "Romantic", "consorts"),
        Social("romantic-paramour", "Paramour", "Romantic", "paramours"),
        Social("romantic-on-off-lover", "On-and-off lover", "Romantic", "on and off lovers"),
        Social("romantic-estranged-spouse", "Estranged spouse", "Romantic", "estranged husband", "estranged wife"),
        Social("romantic-courting-partner", "Courting partner", "Romantic", "courtship", "courting partners"),
        Social("romantic-former-courting-partner", "Former courting partner", "Romantic", "former courtship"),

        .. Roles("societal-master", "Master", "societal-apprentice", "Apprentice", "Societal",
            ["craft master", "artisan master", "artisan"], ["craft apprentice", "artisan apprentice"]),
        .. Roles("societal-teacher", "Teacher", "societal-pupil", "Pupil", "Societal"),
        Social("societal-political-ally", "Political ally", "Societal", "political allies"),
        Social("societal-political-rival", "Political rival", "Societal", "political rivals"),
        .. Roles("societal-liege", "Liege", "societal-vassal", "Vassal", "Societal", ["feudal lord", "lord"]),
        .. Roles("societal-ruler", "Ruler", "societal-subject", "Subject", "Societal", ["sovereign"]),
        .. Roles("societal-commander", "Commander", "societal-subordinate", "Subordinate", "Societal"),
        Social("societal-comrade-in-arms", "Comrade-in-arms", "Societal", "comrades in arms"),
        .. Roles("societal-knightly-master", "Knightly master", "societal-squire", "Squire", "Societal", ["knight"]),
        .. Roles("societal-household-employer", "Household employer", "societal-servant", "Servant", "Societal",
            ["employer of servant"], ["household servant"]),
        .. Roles("societal-patron", "Patron", "societal-protege", "Protégé", "Societal", rightAliases: ["protege"]),
        Social("societal-clanmate", "Clanmate", "Societal", "clan member", "fellow clan member"),
        .. Roles("societal-noble-patron", "Noble patron", "societal-retainer", "Retainer", "Societal", ["noble employer"]),
        .. Roles("societal-household-lord", "Household lord", "societal-steward", "Steward", "Societal", ["lord of steward"]),
        .. Roles("societal-knights-liege", "Knight's liege", "societal-sworn-knight", "Sworn knight", "Societal", ["lord of sworn knight"]),
        .. Roles("societal-noble-contact", "Noble contact", "societal-commoner-contact", "Commoner contact", "Societal",
            ["noble"], ["commoner"]),
        .. Roles("societal-advised-monarch", "Advised monarch", "societal-royal-advisor", "Royal advisor", "Societal",
            ["monarch"], ["royal adviser"]),
        .. Roles("societal-regent", "Regent", "societal-represented-monarch", "Represented monarch", "Societal",
            ["regent for monarch"], ["monarch under regency"]),
        .. Roles("societal-heirs-regent", "Heir's regent", "societal-governed-heir", "Heir under regency", "Societal",
            ["regent for heir"], ["governed heir"]),
        .. Roles("societal-knights-patron", "Knight's patron", "societal-sponsored-knight", "Sponsored knight", "Societal",
            ["patron of knight"], ["patron's knight"]),
        .. Roles("societal-merchant-buyer", "Merchant buyer", "societal-supplier", "Supplier", "Societal",
            rightText: "Supplier to"),
        .. Roles("societal-merchant", "Merchant", "societal-customer", "Customer", "Societal", ["merchant seller"]),
        .. Roles("societal-bodyguard", "Bodyguard", "societal-protected-charge", "Protected charge", "Societal",
            rightAliases: ["bodyguard's charge", "charge"]),
        .. Roles("societal-captain", "Captain", "societal-soldier", "Soldier", "Societal"),
        .. Roles("societal-veteran-mentor", "Veteran mentor", "societal-recruit", "Recruit", "Societal", ["veteran"]),
        .. Roles("societal-mercenary-employer", "Mercenary employer", "societal-hired-mercenary", "Hired mercenary", "Societal",
            rightAliases: ["mercenary"]),
        Social("societal-diplomatic-ally", "Diplomatic ally", "Societal", "diplomatic allies"),
        Social("societal-treaty-partner", "Treaty partner", "Societal", "treaty partners"),
        Social("societal-fellow-courtier", "Fellow courtier", "Societal", "fellow courtiers")
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
        var normalized = Normalize(typed);
        return new[] { definition.Category, definition.DisplayName, definition.Id }
            .Concat(definition.Aliases).Any(value => Normalize(value).Contains(normalized, StringComparison.Ordinal));
    }

    public static RelationshipDefinition? ResolveRequest(string? category, string value)
    {
        var definition = Resolve(value);
        return definition is { Requestable: true } && definition.Category == category ? definition : null;
    }

    public static IEnumerable<RelationshipDefinition> RequestChoices(
        string? category, string typed, IReadOnlySet<string>? suggested = null)
    {
        var exactTypeId = ResolveRequest(category, typed)?.Id;
        return Definitions
            .Where(definition => definition.Requestable && definition.Category == category && MatchesSearch(definition, typed))
            .OrderByDescending(definition => definition.Id == exactTypeId)
            .ThenByDescending(definition => suggested?.Contains(definition.Id) == true)
            .ThenBy(definition => definition.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    // These metadata are also shipped as a static site asset, so a newer website can
    // read older published snapshots without losing the expanded labels or arrows.
    public static IReadOnlyList<PublicRelationshipTypeRecord> PublicTypes() => Definitions
        .Select(definition => new PublicRelationshipTypeRecord
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            Category = definition.Category,
            InverseId = definition.InverseId,
            PerspectiveLabel = definition.PerspectiveLabel ?? (definition.Id switch
            {
                "romantic-engaged" => "Betrothed to",
                "romantic-former-betrothed" => "Former betrothed of",
                "romantic-arranged-betrothed" => "Arranged betrothed to",
                "social-chosen-family" => "Chosen family of",
                _ => DefaultPerspectiveLabel(definition.DisplayName)
            }),
            PreferDirection = definition.PreferDirection || definition.Id is
                "biological-child" or "biological-grandchild" or "biological-great-grandchild" or
                "biological-descendant" or "biological-nibling" or "adoptive-child" or
                "societal-student" or "societal-ward" or "societal-follower" or "societal-employee"
        })
        .OrderBy(type => type.Category, StringComparer.Ordinal)
        .ThenBy(type => type.Id, StringComparer.Ordinal)
        .ToArray();

    private static string DefaultPerspectiveLabel(string displayName)
    {
        var label = displayName.Replace("Biological ", "", StringComparison.Ordinal);
        return char.ToUpperInvariant(label[0]) + label[1..] + " of";
    }

    private static IEnumerable<RelationshipDefinition> Roles(
        string leftId, string leftLabel, string rightId, string rightLabel, string category,
        string[]? leftAliases = null, string[]? rightAliases = null,
        string? leftText = null, string? rightText = null)
    {
        yield return new(leftId, leftLabel, rightId, category, true, leftAliases ?? [], leftText);
        yield return new(rightId, rightLabel, leftId, category, true, rightAliases ?? [], rightText, true);
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

    private static RelationshipDefinition Social(string id, string label, string category, params string[] aliases) =>
        Pair(id, label, id, category, aliases);

    private static RelationshipDefinition Pair(string id, string label, string inverse, string category, params string[] aliases) =>
        new(id, label, inverse, category, true, aliases);

    private static RelationshipInferenceRule Rule(
        string id,
        IReadOnlyList<string> path,
        string result,
        string explanation) => new(id, path, result, explanation);

    private static string Normalize(string value) =>
        new(value.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
