using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class PublicSiteExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly HashSet<string> PrivatePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "approvedby", "discordid", "discorduserid", "guildid", "moderation", "ocroleindex",
        "ownerid", "publicuserid", "roleid", "userid", "verification", "verified"
    };

    private readonly CharacterStore _characters;
    private readonly RelationshipStore _relationships;
    private readonly RelationshipInferenceEngine _inference;

    public PublicSiteExporter(
        CharacterStore characters,
        RelationshipStore relationships,
        RelationshipInferenceEngine inference)
    {
        _characters = characters;
        _relationships = relationships;
        _inference = inference;
    }

    public async Task<string> BuildJsonAsync(ulong guildId)
    {
        var characters = await _characters.GetAllAsync(guildId);
        var characterIds = characters.Select(character => character.PublicId).ToHashSet();
        var relationships = _inference.Build(await _relationships.GetDirectAsync(guildId))
            .Where(edge => characterIds.Contains(edge.SourceCharacterId) &&
                           characterIds.Contains(edge.TargetCharacterId))
            .Select(ToPublicRelationship)
            .Where(record => record is not null)
            .Cast<PublicRelationshipRecord>()
            .OrderBy(record => record.SourceCharacterId)
            .ThenBy(record => record.TargetCharacterId)
            .ThenBy(record => record.IsInferred)
            .ThenBy(record => record.TypeId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var snapshot = new PublicCharacterSnapshot
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Characters = characters
                .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToPublicRecord)
                .ToList(),
            RelationshipTypes = relationships
                .Select(record => new PublicRelationshipTypeRecord
                {
                    Id = record.TypeId,
                    DisplayName = record.DisplayName,
                    Category = record.Category
                })
                .DistinctBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(record => record.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Relationships = relationships
        };
        return JsonSerializer.Serialize(snapshot, JsonOptions) + Environment.NewLine;
    }

    private static PublicRelationshipRecord? ToPublicRelationship(RelationshipEdge edge)
    {
        var definition = RelationshipCatalog.Get(edge.TypeId);
        if (definition is null) return null;
        return new PublicRelationshipRecord
        {
            SourceCharacterId = edge.SourceCharacterId,
            TargetCharacterId = edge.TargetCharacterId,
            TypeId = definition.Id,
            DisplayName = definition.DisplayName,
            Category = definition.Category,
            IsInferred = edge.IsInferred,
            Explanation = edge.IsInferred ? edge.Explanation : null
        };
    }

    private static PublicCharacterRecord ToPublicRecord(Character character)
    {
        var properties = character.AdditionalProperties
            .Where(pair => !IsPrivateProperty(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        return new PublicCharacterRecord
        {
            PublicId = character.PublicId,
            Name = character.Name,
            Aliases = character.Aliases
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Age = character.Age,
            Gender = character.Gender,
            Region = character.Region,
            Occupation = character.Occupation,
            ApprovedAt = character.ApprovedAt,
            Reference = ToPublicReference(character.CharacterReference),
            Properties = properties
        };
    }

    private static PublicCharacterReference? ToPublicReference(CharacterReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.Value)) return null;
        if (!Uri.TryCreate(reference.Value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return null;
        return new PublicCharacterReference
        {
            Kind = reference.Kind,
            Format = reference.Format,
            Value = uri.AbsoluteUri
        };
    }

    private static bool IsPrivateProperty(string propertyName)
    {
        var normalized = new string(propertyName.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return PrivatePropertyNames.Contains(normalized) ||
               normalized.Contains("discord", StringComparison.Ordinal) ||
               normalized.Contains("moderation", StringComparison.Ordinal) ||
               normalized.Contains("verification", StringComparison.Ordinal) ||
               normalized.EndsWith("userid", StringComparison.Ordinal) ||
               normalized.EndsWith("roleid", StringComparison.Ordinal);
    }
}
