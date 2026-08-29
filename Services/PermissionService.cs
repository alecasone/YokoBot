using Discord;
using Discord.WebSocket;

namespace Yoko.Bot.Services;

internal sealed class PermissionService
{
    private readonly PermissionStore _store;

    public PermissionService(PermissionStore store) => _store = store;

    public async Task<bool> HasAsync(ulong guildId, IUser user, string permission)
    {
        if (user is SocketGuildUser { GuildPermissions.Administrator: true }) return true;
        var grants = await _store.GetGrantsAsync(guildId);
        return HasAny(grants, user, [permission]);
    }

    public async Task<bool> HasAnyAsync(ulong guildId, IUser user, IEnumerable<string> permissions)
    {
        if (user is SocketGuildUser { GuildPermissions.Administrator: true }) return true;
        var grants = await _store.GetGrantsAsync(guildId);
        return HasAny(grants, user, permissions);
    }

    private static bool HasAny(
        IReadOnlyDictionary<string, Yoko.Bot.Models.PermissionGrant> grants,
        IUser user,
        IEnumerable<string> permissions)
    {
        var roleIds = user is SocketGuildUser guildUser
            ? guildUser.Roles.Select(role => role.Id).ToHashSet()
            : [];
        var requested = permissions.ToArray();
        return grants.Any(pair =>
            requested.Any(permission => PermissionCatalog.Matches(pair.Key, permission)) &&
            (pair.Value.UserIds.Contains(user.Id) || pair.Value.RoleIds.Any(roleIds.Contains)));
    }
}
