namespace Yoko.Bot.Models;

internal static class CharacterSchema
{
    public static readonly string[] InitialDefaultProperties =
        ["age", "gender", "region", "occupation", "reference"];

    public static readonly string[] ReservedProperties =
        ["name", "approved-at", "approved-by", "reference-kind", "reference-format"];

    public static string Label(string property) =>
        string.Join(' ', property.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    public static string Normalize(string property) =>
        string.Join('-', property.Trim().ToLowerInvariant()
            .Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));
}

internal sealed class CharacterGuildSettings
{
    public List<string> DefaultProperties { get; set; } = [.. CharacterSchema.InitialDefaultProperties];
    public Dictionary<string, List<string>> AutofillValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
