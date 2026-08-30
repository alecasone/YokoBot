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

    public PublicSiteExporter(CharacterStore characters) => _characters = characters;

    public async Task<string> BuildJsonAsync(ulong guildId)
    {
        var characters = await _characters.GetAllAsync(guildId);
        var snapshot = new PublicCharacterSnapshot
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Characters = characters
                .Where(character => !character.IsTestFixture)
                .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToPublicRecord)
                .ToList()
        };
        return JsonSerializer.Serialize(snapshot, JsonOptions) + Environment.NewLine;
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
