using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class PermissionCommands
{
    public static ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName("permissions")
            .WithDescription("Views and edits Yoko's PEX-style command permissions.")
            .AddOption(RoleMutation("grant", "Grants a permission to a Discord role."))
            .AddOption(RoleMutation("revoke", "Revokes a permission from a Discord role."))
            .AddOption(UserMutation("grant-user", "Grants a permission directly to one member."))
            .AddOption(UserMutation("revoke-user", "Revokes a direct permission from one member."))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("view")
                .WithDescription("Shows who receives a permission, including wildcard grants.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(PermissionOption()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("role")
                .WithDescription("Shows all permissions assigned directly to a role.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("role", ApplicationCommandOptionType.Role, "Discord role", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list")
                .WithDescription("Lists every permission Yoko recognizes.")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();

    public static async Task HandleAsync(SocketSlashCommand command, PermissionStore store)
    {
        if (command.GuildId is not { } guildId || command.Channel is not SocketGuildChannel guildChannel)
        {
            await command.RespondAsync("Permission commands can only be used in a server.", ephemeral: true);
            return;
        }

        var subcommand = command.Data.Options.First();
        switch (subcommand.Name)
        {
            case "grant":
            case "revoke":
            {
                var permission = ReadPermission(subcommand);
                if (!await ValidatePermissionAsync(command, permission)) return;
                var role = (IRole)Option(subcommand.Options, "role").Value;
                var changed = subcommand.Name == "grant"
                    ? await store.GrantRoleAsync(guildId, permission, role.Id)
                    : await store.RevokeRoleAsync(guildId, permission, role.Id);
                await command.RespondAsync(
                    changed
                        ? $"{(subcommand.Name == "grant" ? "Granted" : "Revoked")} `{permission}` {(subcommand.Name == "grant" ? "to" : "from")} {role.Mention}."
                        : $"No change: {role.Mention} {(subcommand.Name == "grant" ? "already has" : "does not directly have")} `{permission}`.",
                    ephemeral: true,
                    allowedMentions: AllowedMentions.None);
                return;
            }
            case "grant-user":
            case "revoke-user":
            {
                var permission = ReadPermission(subcommand);
                if (!await ValidatePermissionAsync(command, permission)) return;
                var user = (IUser)Option(subcommand.Options, "user").Value;
                var changed = subcommand.Name == "grant-user"
                    ? await store.GrantUserAsync(guildId, permission, user.Id)
                    : await store.RevokeUserAsync(guildId, permission, user.Id);
                await command.RespondAsync(
                    changed
                        ? $"{(subcommand.Name == "grant-user" ? "Granted" : "Revoked")} `{permission}` {(subcommand.Name == "grant-user" ? "to" : "from")} {user.Mention}."
                        : $"No change: {user.Mention} {(subcommand.Name == "grant-user" ? "already has" : "does not directly have")} `{permission}`.",
                    ephemeral: true,
                    allowedMentions: AllowedMentions.None);
                return;
            }
            case "view":
            {
                var permission = ReadPermission(subcommand);
                if (!await ValidatePermissionAsync(command, permission)) return;
                var grants = await store.GetGrantsAsync(guildId);
                var matching = grants
                    .Where(pair => PermissionCatalog.Matches(pair.Key, permission))
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var lines = new List<string> { $"**Effective grants for `{permission}`**" };
                if (command.User is SocketGuildUser { GuildPermissions.Administrator: true })
                    lines.Add("Discord server Administrators always bypass Yoko's permission file.");
                if (matching.Length == 0) lines.Add("No role or user grants match this permission.");
                foreach (var (grantedName, grant) in matching)
                {
                    var roles = grant.RoleIds.Select(id => guildChannel.Guild.GetRole(id)?.Mention ?? $"deleted role `{id}`");
                    var users = grant.UserIds.Select(id => guildChannel.Guild.GetUser(id)?.Mention ?? $"user `{id}`");
                    var principals = roles.Concat(users).ToArray();
                    lines.Add($"- `{grantedName}` → {(principals.Length == 0 ? "nobody" : string.Join(", ", principals))}");
                }
                await RespondPagesAsync(command, lines);
                return;
            }
            case "role":
            {
                var role = (IRole)Option(subcommand.Options, "role").Value;
                var grants = await store.GetGrantsAsync(guildId);
                var names = grants
                    .Where(pair => pair.Value.RoleIds.Contains(role.Id))
                    .Select(pair => pair.Key)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var lines = new List<string> { $"**Direct permissions for {role.Mention}**" };
                lines.AddRange(names.Length == 0 ? ["None."] : names.Select(name => $"- `{name}`"));
                await RespondPagesAsync(command, lines);
                return;
            }
            case "list":
            {
                var grants = await store.GetGrantsAsync(guildId);
                var lines = new List<string>
                {
                    "**Yoko permission catalog**",
                    "A grant such as `character.*` covers every permission beginning with `character.`. `*` covers everything. Discord server Administrators always bypass this file."
                };
                foreach (var definition in PermissionCatalog.Definitions)
                {
                    lines.Add($"**`{definition.Name}`** — {definition.Description}");
                    lines.Add($"Access: {FormatEffectiveAccess(guildChannel.Guild, grants, definition.Name)}");
                }
                await RespondPagesAsync(command, lines);
                return;
            }
        }
    }

    public static async Task HandleAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        if (interaction.Data.Current.Name != "permission")
        {
            await interaction.RespondAsync([]);
            return;
        }

        var typed = interaction.Data.Current.Value?.ToString() ?? string.Empty;
        await interaction.RespondAsync(PermissionCatalog.AssignableNames
            .Where(name => name.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(name => new AutocompleteResult(name, name)));
    }

    private static SlashCommandOptionBuilder RoleMutation(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(PermissionOption())
            .AddOption("role", ApplicationCommandOptionType.Role, "Discord role", isRequired: true);

    private static SlashCommandOptionBuilder UserMutation(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(PermissionOption())
            .AddOption("user", ApplicationCommandOptionType.User, "Discord member", isRequired: true);

    private static SlashCommandOptionBuilder PermissionOption() =>
        new SlashCommandOptionBuilder()
            .WithName("permission")
            .WithDescription("Exact permission, a section wildcard such as character.*, or *")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .WithAutocomplete(true);

    private static string ReadPermission(SocketSlashCommandDataOption subcommand) =>
        PermissionCatalog.Normalize((string)Option(subcommand.Options, "permission").Value);

    private static async Task<bool> ValidatePermissionAsync(SocketSlashCommand command, string permission)
    {
        if (PermissionCatalog.IsKnownOrWildcard(permission)) return true;
        await command.RespondAsync(
            $"`{permission}` is not a recognized permission or wildcard. Use `/permissions list`.",
            ephemeral: true);
        return false;
    }

    private static async Task RespondPagesAsync(SocketSlashCommand command, IEnumerable<string> lines)
    {
        var pages = new List<string>();
        var page = string.Empty;
        foreach (var line in lines)
        {
            if (page.Length > 0 && page.Length + line.Length + 1 > 1850)
            {
                pages.Add(page);
                page = string.Empty;
            }
            page += (page.Length == 0 ? string.Empty : "\n") + line;
        }
        if (page.Length > 0) pages.Add(page);
        if (pages.Count == 0) pages.Add("Nothing to show.");

        await command.RespondAsync(pages[0], ephemeral: true, allowedMentions: AllowedMentions.None);
        foreach (var continuation in pages.Skip(1))
            await command.FollowupAsync(continuation, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    private static SocketSlashCommandDataOption Option(
        IReadOnlyCollection<SocketSlashCommandDataOption> options,
        string name) => options.First(option => option.Name == name);

    private static string FormatEffectiveAccess(
        SocketGuild guild,
        IReadOnlyDictionary<string, PermissionGrant> grants,
        string requestedPermission)
    {
        var principals = new Dictionary<string, EffectivePrincipal>(StringComparer.Ordinal);
        foreach (var (grantedPermission, grant) in grants.Where(pair =>
                     PermissionCatalog.Matches(pair.Key, requestedPermission)))
        {
            foreach (var roleId in grant.RoleIds)
            {
                var key = $"role:{roleId}";
                if (!principals.TryGetValue(key, out var principal))
                {
                    var display = guild.GetRole(roleId)?.Mention ?? $"deleted role `{roleId}`";
                    principals[key] = principal = new EffectivePrincipal(display);
                }
                principal.Sources.Add(grantedPermission);
            }

            foreach (var userId in grant.UserIds)
            {
                var key = $"user:{userId}";
                if (!principals.TryGetValue(key, out var principal))
                {
                    var display = guild.GetUser(userId)?.Mention ?? $"user `{userId}`";
                    principals[key] = principal = new EffectivePrincipal(display);
                }
                principal.Sources.Add(grantedPermission);
            }
        }

        if (principals.Count == 0) return "none assigned in `permissions.json`";

        var formatted = principals.Values
            .OrderBy(principal => principal.Display, StringComparer.OrdinalIgnoreCase)
            .Select(principal =>
            {
                var inherited = principal.Sources
                    .Where(source => !source.Equals(requestedPermission, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return inherited.Length == 0
                    ? principal.Display
                    : $"{principal.Display} via {string.Join(" / ", inherited.Select(source => $"`{source}`"))}";
            });
        var result = string.Join(", ", formatted);
        return result.Length <= 1100 ? result : result[..1097] + "...";
    }

    private sealed record EffectivePrincipal(string Display)
    {
        public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
