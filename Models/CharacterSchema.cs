namespace Yoko.Bot.Models;

internal static class CharacterSchema
{
    public static readonly string[] InitialDefaultProperties =
        ["age", "gender", "region", "occupation", "reference"];

    public static readonly string[] ReservedProperties =
        ["name", "approved-at", "approved-by", "oc-role-index", "reference-kind", "reference-format"];

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
