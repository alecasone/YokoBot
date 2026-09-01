using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal static class PermissionCatalog
{
    public const ulong AdminRoleId = 1541979162242195466;
    public const ulong ModeratorRoleId = 1541979611754135643;
    public const ulong VerifiedRoleId = 1542018931894521887;

    public static readonly IReadOnlyList<PermissionDefinition> Definitions =
    [
        new("ping", "Use /ping."),
        new("bot.shutdown", "Shut down Yoko."),
        new("character.approve", "Approve a character for any member."),
        new("character.edit.self", "Edit or remove fields from your own characters."),
        new("character.edit.any", "Edit or remove fields from any member's characters."),
        new("character.view.self", "View your own characters."),
        new("character.view.any", "View any member's characters."),
        new("character.delete.self", "Delete your own characters."),
        new("character.delete.any", "Delete any member's characters."),
        new("character.configure.properties", "Configure default character properties."),
        new("character.configure.autofill", "Configure character autofill values."),
        new("character.configure.roles", "Configure character approval and OC roles."),
        new("character.configure.approval-messages", "Configure character approval messages."),
        new("verification.verify", "Verify a member with a verification profile."),
        new("verification.configure.roles", "Create, edit, and delete verification profiles."),
        new("verification.configure.success-message", "Configure verification success messages."),
        new("automod.add", "Create auto-moderation rules."),
        new("automod.delete", "Delete auto-moderation rules."),
        new("automod.view", "View auto-moderation rules."),
        new("automod.approve", "Confirm or cancel queued auto-moderation actions."),
        new("alerts.view", "View configured member join and leave alerts."),
        new("alerts.configure.leave", "Configure role-based member-leave alerts."),
        new("alerts.configure.new-account", "Configure alerts for newly created accounts."),
        new("debug.recheck-verified", "Run the verification role reconciliation scan."),
        new("overworld.worlddate", "Set the current world date."),
        new("scenetracker.create", "Create a scene with one of your characters."),
        new("scenetracker.view", "View active scene details."),
        new("scenetracker.history", "View current and completed scene history."),
        new("scenetracker.manage.own", "Manage scenes in which you participate."),
        new("scenetracker.manage.any", "Manage any scene without being a participant."),
        new("permissions.view", "View the permission catalog and assignments."),
        new("permissions.manage", "Grant and revoke permissions."),
        new("site.view", "View GitHub Pages publishing status."),
        new("site.publish", "Publish the sanitized character directory."),
        new("site.configure", "Configure GitHub Pages publishing."),
        new("relationship.request", "Request relationships between owned characters."),
        new("relationship.respond", "List, approve, and decline incoming relationship requests."),
        new("relationship.remove", "Remove direct relationships involving an owned character."),
        new("relationship.view", "View direct and inferred character relationships."),
    ];

    public static IReadOnlyList<string> AssignableNames { get; } = BuildAssignableNames();

    public static bool IsKnownOrWildcard(string permission)
    {
        var normalized = Normalize(permission);
        if (normalized == "*") return true;
        if (Definitions.Any(definition => definition.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (!normalized.EndsWith(".*", StringComparison.Ordinal)) return false;

        var prefix = normalized[..^2];
        return Definitions.Any(definition =>
            definition.Name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
    }

    public static bool Matches(string grantedPermission, string requestedPermission)
    {
        var granted = Normalize(grantedPermission);
        var requested = Normalize(requestedPermission);
        if (granted == "*") return true;
        if (granted.Equals(requested, StringComparison.OrdinalIgnoreCase)) return true;
        if (!granted.EndsWith(".*", StringComparison.Ordinal)) return false;

        var prefix = granted[..^2];
        return requested.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string permission) => permission.Trim().ToLowerInvariant();

    public static IReadOnlyDictionary<string, PermissionGrant> CreateSeedGrants()
    {
        var grants = new Dictionary<string, PermissionGrant>(StringComparer.OrdinalIgnoreCase);

        AddRole(grants, "*", AdminRoleId);

        foreach (var permission in new[]
                 {
                     "ping",
                     "character.approve",
                     "character.edit.any",
                     "character.view.any",
                     "character.delete.any",
                     "verification.verify",
                     "automod.view",
                     "automod.approve",
                     "alerts.view",
                     "debug.recheck-verified",
                     "scenetracker.*",
                     "relationship.request",
                     "relationship.respond",
                     "relationship.remove",
                     "relationship.view",
                     "permissions.view"
                 })
            AddRole(grants, permission, ModeratorRoleId);

        foreach (var permission in new[]
                 {
                     "ping",
                     "character.edit.self",
                     "character.view.self",
                     "character.delete.self",
                     "scenetracker.create",
                     "scenetracker.view",
                     "scenetracker.history",
                     "scenetracker.manage.own",
                     "relationship.request",
                     "relationship.respond",
                     "relationship.remove",
                     "relationship.view"
                 })
            AddRole(grants, permission, VerifiedRoleId);

        return grants;
    }

    private static IReadOnlyList<string> BuildAssignableNames()
    {
        var names = Definitions.Select(definition => definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        names.Add("*");
        foreach (var definition in Definitions)
        {
            var pieces = definition.Name.Split('.');
            for (var index = 1; index < pieces.Length; index++)
                names.Add(string.Join('.', pieces.Take(index)) + ".*");
        }

        return names
            .Where(IsKnownOrWildcard)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddRole(Dictionary<string, PermissionGrant> grants, string permission, ulong roleId)
    {
        if (!grants.TryGetValue(permission, out var grant))
            grants[permission] = grant = new PermissionGrant();
        grant.RoleIds.Add(roleId);
    }
}
