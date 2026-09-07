namespace Yoko.Bot.Models;

internal static class CharacterSchema
{
    // Discord autocomplete choice names and string values are limited to 100 characters.
    public const int NameMaxLength = 100;
    public const int AutocompleteLabelMaxLength = 100;
    public const int PropertyNameMaxLength = 100;
    public const int AutofillValueMaxLength = 100;
    public const int ApprovalTemplateMaxLength = 1800;
    private const string SelectorPrefix = "character:";

    public static readonly string[] InitialDefaultProperties =
        ["age", "gender", "region", "occupation", "reference"];

    public static readonly string[] ReservedProperties =
        ["name", "public-id", "approved-at", "approved-by", "oc-role-index", "reference-kind", "reference-format"];

    public static string Label(string property) =>
        string.Join(' ', property.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    public static string Normalize(string property) =>
        string.Join('-', property.Trim().ToLowerInvariant()
            .Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));

    public static bool TryNormalizeName(string? value, out string name)
    {
        name = value?.Trim() ?? string.Empty;
        return name.Length is > 0 and <= NameMaxLength &&
               !name.StartsWith(SelectorPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryNormalizeProperty(string? value, out string property)
    {
        property = string.IsNullOrWhiteSpace(value) ? string.Empty : Normalize(value);
        return property.Length is > 0 and <= PropertyNameMaxLength;
    }

    public static string Selector(Guid publicId) => $"{SelectorPrefix}{publicId:N}";

    public static bool TryParseSelector(string? value, out Guid publicId)
    {
        publicId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(SelectorPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return Guid.TryParseExact(value[SelectorPrefix.Length..], "N", out publicId);
    }

    public static string BoundedName(string value)
    {
        if (value.Length <= AutocompleteLabelMaxLength) return value;
        return value[..(AutocompleteLabelMaxLength - 3)] + "...";
    }
}

internal sealed class CharacterGuildSettings
{
    public List<string> DefaultProperties { get; set; } = [.. CharacterSchema.InitialDefaultProperties];
    public Dictionary<string, List<string>> AutofillValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ulong> OcDefaultRoleIds { get; set; } = [];
    public List<ulong> OcRoleIds { get; set; } = [];
    public List<ulong> OcRemovedRoleIds { get; set; } = [];
    public List<CharacterApprovalMessage> ApprovalMessages { get; set; } = [];
}

internal sealed class CharacterApprovalMessage
{
    public string Destination { get; set; } = "channel";
    public ulong? ChannelId { get; set; }
    public string Template { get; set; } = string.Empty;
}

internal sealed record CharacterRoleConfiguration(
    IReadOnlyList<ulong> DefaultRoleIds,
    IReadOnlyList<ulong> SequentialRoleIds,
    IReadOnlyList<ulong> RemovedRoleIds);
