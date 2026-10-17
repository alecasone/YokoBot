using System.Globalization;
using System.Text;
using System.Text.Json;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

internal static class RelationshipChecks
{
    internal static async Task RunAsync(string directory, Action<bool, string> check)
    {
        var catalog = RelationshipCatalog.Definitions;
        var terms = new Dictionary<string, string>();
        foreach (var type in catalog)
        {
            var inverse = RelationshipCatalog.Get(type.InverseId);
            check(inverse?.InverseId == type.Id && inverse.Category == type.Category, $"Broken inverse for {type.Id}.");
            check(type.Category is "Biological" or "Adoptive" or "Social" or "Romantic" or "Societal", $"Unexpected category: {type.Id}.");
            foreach (var term in type.Aliases.Prepend(type.DisplayName).Prepend(type.Id))
            {
                var normalized = new string(term.Normalize(NormalizationForm.FormD)
                    .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                check(!terms.TryGetValue(normalized, out var existing) || existing == type.Id,
                    $"Ambiguous catalog term '{term}': {existing} and {type.Id}.");
                terms[normalized] = type.Id;
                check(RelationshipCatalog.Resolve(term)?.Id == type.Id, $"Unreachable term '{term}'.");
                check(RelationshipCatalog.MatchesSearch(type, term), $"Autocomplete cannot find '{term}'.");
            }
        }

        foreach (var (term, expected) in new (string, string)[] {
            ("mentor", "societal-mentor"), ("mentee", "societal-student"), ("student", "societal-student"),
            ("teacher", "societal-teacher"), ("pupil", "societal-pupil"), ("master", "societal-master"),
            ("apprentice", "societal-apprentice"), ("artisan master", "societal-master"),
            ("commander", "societal-commander"), ("vassal", "societal-vassal"), ("subject", "societal-subject"),
            ("betrothed", "romantic-engaged"), ("fiancée", "romantic-engaged"), ("husband", "romantic-spouse"),
            ("ex spouse", "romantic-former-spouse"), ("protege", "societal-protege"),
            ("childhood companion", "social-childhood-companion"), ("childhood friend", "social-childhood-friend") })
            check(RelationshipCatalog.Resolve(term)?.Id == expected, $"Incorrect medieval alias: {term}.");
        foreach (var excluded in new[] { "crush", "secret admirer", "unrequited love", "magical bond", "feuding house", "rival house" })
            check(RelationshipCatalog.Resolve(excluded) is null, $"Out-of-scope relation added: {excluded}.");

        var publicTypes = RelationshipCatalog.PublicTypes();
        foreach (var type in publicTypes)
        {
            check(!string.IsNullOrWhiteSpace(type.PerspectiveLabel), $"Missing public perspective: {type.Id}.");
            var inverse = publicTypes.Single(candidate => candidate.Id == type.InverseId);
            check(type.Id == type.InverseId ? !type.PreferDirection : type.PreferDirection != inverse.PreferDirection,
                $"Arrow orientation is ambiguous: {type.Id}.");
        }
        var sourceDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (sourceDirectory is not null && !File.Exists(Path.Combine(sourceDirectory.FullName, "Yoko.Bot.csproj")))
            sourceDirectory = sourceDirectory.Parent;
        check(sourceDirectory is not null, "Could not locate the repository catalog asset.");
        var shipped = JsonSerializer.Deserialize<List<PublicRelationshipTypeRecord>>(
            await File.ReadAllTextAsync(Path.Combine(sourceDirectory!.FullName, "docs", "relationship-catalog.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        check(JsonSerializer.Serialize(shipped) == JsonSerializer.Serialize(publicTypes), "Static website catalog drifted from the bot. Regenerate it.");

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        RelationshipRecord Fact(Guid source, Guid target, string type) => new()
        {
            SourceCharacterId = source, SourceOwnerId = 10, TargetCharacterId = target, TargetOwnerId = 20, TypeId = type
        };
        var engine = new RelationshipInferenceEngine();
        // Every explicit non-biological relation produces exactly its two perspectives, with no extra inferences.
        foreach (var type in catalog.Where(type => type.Category != "Biological"))
        {
            var edges = engine.Build([Fact(a, b, type.Id)]);
            check(edges.Count == 2 && edges.All(edge => !edge.IsInferred), $"Unexpected inference for {type.Id}.");
            check(edges.Any(edge => edge.SourceCharacterId == a && edge.TypeId == type.Id) &&
                  edges.Any(edge => edge.SourceCharacterId == b && edge.TypeId == type.InverseId), $"Wrong perspectives for {type.Id}.");
        }
        var siblings = engine.Build([Fact(c, a, "biological-parent"), Fact(c, b, "biological-parent")]);
        check(siblings.Any(edge => edge.SourceCharacterId == a && edge.TargetCharacterId == b &&
                                  edge.TypeId == "biological-sibling" && edge.IsInferred), "Shared parent did not infer siblinghood.");
        check(!siblings.Any(edge => edge.TypeId is "biological-full-sibling" or "biological-half-sibling" or "biological-twin"),
            "Incomplete parentage invented full siblings, half siblings, or twins.");
        check(!engine.Build([Fact(c, a, "biological-parent")]).Any(edge => edge.TypeId == "biological-sibling"),
            "Unsupported inferred sibling survived fact removal.");
        var explicitSibling = engine.Build([Fact(c, a, "biological-parent"), Fact(a, b, "biological-sibling")]);
        check(explicitSibling.Any(edge => edge.TypeId == "biological-sibling" && !edge.IsInferred),
            "Explicit sibling was lost after its inferred support disappeared.");
        var mixed = engine.Build([Fact(c, a, "biological-parent"), Fact(c, b, "romantic-spouse"),
            Fact(a, b, "societal-guardian"), Fact(a, b, "societal-foster-parent"), Fact(a, b, "social-sworn-sibling")]);
        check(mixed.All(edge => !edge.IsInferred), "Marriage, guardianship, foster care, or sworn family invented another family tie.");

        var store = new RelationshipStore(Path.Combine(directory, "expanded-relationships.json"));
        async Task AddApproved(string type)
        {
            var request = new PendingRelationshipRequest
            {
                SourceCharacterId = a, SourceOwnerId = 10, TargetCharacterId = b, TargetOwnerId = 20, TypeId = type
            };
            var before = (await store.GetDirectAsync(1)).Count;
            check(await store.AddPendingAsync(1, request) == RelationshipMutationStatus.Success, $"Request rejected: {type}.");
            check((await store.GetDirectAsync(1)).Count == before, "Pending relationship became public.");
            check((await store.ApproveAsync(1, request.Id, 10)).Status == RelationshipMutationStatus.NotAuthorized, "Source owner bypassed consent.");
            check((await store.ApproveAsync(1, request.Id, 20)).Status == RelationshipMutationStatus.Success, "Target owner could not approve.");
        }
        foreach (var type in new[] { "social-friendly-rival", "social-close-friend", "romantic-spouse", "romantic-former-spouse", "societal-master" })
            await AddApproved(type);
        check((await store.GetDirectAsync(1)).Count == 5, "New ties replaced coexisting or historical relationships.");
        check(await store.AddPendingAsync(1, new PendingRelationshipRequest
        {
            SourceCharacterId = b, SourceOwnerId = 20, TargetCharacterId = a, TargetOwnerId = 10, TypeId = "societal-apprentice"
        }) == RelationshipMutationStatus.AlreadyExists, "Reversed inverse created an independent duplicate.");

        var exportedTypes = JsonSerializer.Serialize(publicTypes);
        check(!exportedTypes.Contains("OwnerId") && !exportedTypes.Contains("CharacterId"), "Static catalog includes character data.");
    }
}
