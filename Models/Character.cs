using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yoko.Bot.Models;

internal sealed class Character
{
    public Guid PublicId { get; set; }
    public required string Name { get; set; }
    public List<string> Aliases { get; set; } = [];
    public string? Age { get; set; }
    public string? Gender { get; set; }
    public string? Region { get; set; }
    public string? Occupation { get; set; }
    public CharacterReference CharacterReference { get; set; } = new();
    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.UtcNow;
    public ulong ApprovedBy { get; set; }
    public int OcRoleIndex { get; set; }

    // Unknown JSON properties survive load/save cycles and can be managed by admin commands.
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class CharacterReference
{
    public string Kind { get; set; } = "sheet";
    public string Format { get; set; } = "link";
    public string? Value { get; set; }
}

internal sealed class UserCharacters
{
    public List<Character> Characters { get; set; } = [];
}
