using Discord;
using Discord.WebSocket;

namespace Yoko.Bot.Services;

internal static class CommandPermissionResolver
{
    public static IReadOnlyList<string> Resolve(SocketSlashCommand command)
    {
        var root = command.Data.Options.FirstOrDefault();
        return command.Data.Name switch
        {
            "ping" => ["ping"],
            "shutdown" => ["bot.shutdown"],
            "character" => ResolveCharacter(command.User.Id, root),
            "charadmin" => ResolveCharacterAdmin(root),
            "automod" => [$"automod.{root?.Name}"],
            "verify" => ["verification.verify"],
            "verifyadmin" => ResolveVerificationAdmin(root),
            "debug" => ["debug.recheck-verified"],
            "overworld" => ["overworld.worlddate"],
            "scenetracker" => ResolveSceneTracker(root),
            "permissions" => ResolvePermissionAdmin(root),
            "siteadmin" => ResolveSiteAdmin(root),
            "relationship" => ResolveRelationship(root),
            _ => []
        };
    }

    public static IReadOnlyList<string> ResolveAutocomplete(SocketAutocompleteInteraction interaction)
    {
        var targetUserId = ReadUserId(interaction.Data.Options);
        return interaction.Data.CommandName switch
        {
            "character" when targetUserId is { } target && target != interaction.User.Id =>
                ["character.approve", "character.edit.any", "character.view.any", "character.delete.any"],
            "character" =>
                ["character.approve", "character.edit.self", "character.edit.any", "character.view.self", "character.view.any", "character.delete.self", "character.delete.any"],
            "charadmin" =>
                ["character.configure.properties", "character.configure.autofill", "character.configure.roles", "character.configure.approval-messages"],
            "automod" => ["automod.add", "automod.delete", "automod.view"],
            "verify" => ["verification.verify"],
            "verifyadmin" => ["verification.configure.roles", "verification.configure.success-message"],
            "scenetracker" =>
                ["scenetracker.create", "scenetracker.view", "scenetracker.history", "scenetracker.manage.own", "scenetracker.manage.any"],
            "permissions" => ["permissions.view", "permissions.manage"],
            "relationship" => ["relationship.request", "relationship.respond", "relationship.remove", "relationship.view"],
            _ => []
        };
    }

    private static IReadOnlyList<string> ResolveCharacter(ulong actorId, SocketSlashCommandDataOption? subcommand)
    {
        if (subcommand is null) return [];
        if (subcommand.Name == "approve") return ["character.approve"];

        var targetId = ReadUserId(subcommand.Options);
        var scope = targetId == actorId ? "self" : "any";
        return subcommand.Name switch
        {
            "edit" or "remove-field" when scope == "self" => ["character.edit.self", "character.edit.any"],
            "edit" or "remove-field" => ["character.edit.any"],
            "view" when scope == "self" => ["character.view.self", "character.view.any"],
            "view" => ["character.view.any"],
            "delete" when scope == "self" => ["character.delete.self", "character.delete.any"],
            "delete" => ["character.delete.any"],
            _ => []
        };
    }

    private static IReadOnlyList<string> ResolveCharacterAdmin(SocketSlashCommandDataOption? subcommand) =>
        subcommand?.Name switch
        {
            "properties" => ["character.configure.properties"],
            "autofill" => ["character.configure.autofill"],
            "roles" => ["character.configure.roles"],
            "approvemessage" => ["character.configure.approval-messages"],
            _ => []
        };

    private static IReadOnlyList<string> ResolveVerificationAdmin(SocketSlashCommandDataOption? subcommand) =>
        subcommand?.Name switch
        {
            "role" => ["verification.configure.roles"],
            "successmessage" => ["verification.configure.success-message"],
            _ => []
        };

    private static IReadOnlyList<string> ResolveSceneTracker(SocketSlashCommandDataOption? subcommand) =>
        subcommand?.Name switch
        {
            "create" => ["scenetracker.create"],
            "view" => ["scenetracker.view"],
            "history" => ["scenetracker.history"],
            "invite" or "complete" or "delete" or "edit" => ["scenetracker.manage.own", "scenetracker.manage.any"],
            _ => []
        };

    private static IReadOnlyList<string> ResolvePermissionAdmin(SocketSlashCommandDataOption? subcommand) =>
        subcommand?.Name switch
        {
            "list" or "view" or "role" => ["permissions.view", "permissions.manage"],
            "grant" or "revoke" or "grant-user" or "revoke-user" => ["permissions.manage"],
            _ => []
        };

    private static IReadOnlyList<string> ResolveSiteAdmin(SocketSlashCommandDataOption? subcommand) =>
        subcommand?.Name switch
        {
            "status" => ["site.view", "site.publish", "site.configure"],
            "publish" => ["site.publish", "site.configure"],
            "setup" or "autopublish" => ["site.configure"],
            _ => []
        };

    private static IReadOnlyList<string> ResolveRelationship(SocketSlashCommandDataOption? subcommand) =>
        subcommand?.Name switch
        {
            "request" => ["relationship.request"],
            "requests" or "approve" or "decline" => ["relationship.respond"],
            "remove" => ["relationship.remove"],
            "view" => ["relationship.view"],
            _ => []
        };

    private static ulong? ReadUserId(IEnumerable<SocketSlashCommandDataOption> options)
    {
        var value = options.FirstOrDefault(option => option.Name == "user")?.Value;
        if (value is IUser user) return user.Id;
        return ulong.TryParse(value?.ToString(), out var id) ? id : null;
    }

    private static ulong? ReadUserId(IEnumerable<AutocompleteOption> options)
    {
        var value = options.FirstOrDefault(option => option.Name == "user")?.Value;
        if (value is IUser user) return user.Id;
        return ulong.TryParse(value?.ToString(), out var id) ? id : null;
    }
}
