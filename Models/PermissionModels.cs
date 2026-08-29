namespace Yoko.Bot.Models;

internal sealed class PermissionData
{
    public Dictionary<string, GuildPermissionSettings> Guilds { get; set; } = [];
}

internal sealed class GuildPermissionSettings
{
    public int SeedVersion { get; set; }
    public Dictionary<string, PermissionGrant> Grants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PermissionGrant
{
    public List<ulong> RoleIds { get; set; } = [];
    public List<ulong> UserIds { get; set; } = [];
}

internal sealed record PermissionDefinition(string Name, string Description);

