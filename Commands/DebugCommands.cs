using Discord;
using Discord.WebSocket;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class DebugCommands
{
    public static ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName("debug")
            .WithDescription("Runs administrator diagnostics and reconciliation tools.")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("recheck-verified")
                .WithDescription("Reconciles stored verification state with Discord verification roles.")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();

    public static async Task HandleAsync(SocketSlashCommand command, AutoModerationService moderation)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);
        var result = await moderation.RecheckVerificationAsync(guildId);
        var missingRoles = new List<string>();
        if (!result.VerifiedRoleFound) missingRoles.Add("Verified");
        if (!result.UnverifiedRoleFound) missingRoles.Add("Unverified");
        var roleWarning = missingRoles.Count == 0
            ? string.Empty
            : $" Missing Discord role(s): **{string.Join("**, **", missingRoles)}**.";
        var content = $"Verification recheck complete. Scanned **{result.Scanned}** stored users.\n" +
                      $"- Members with only `Verified`: **{result.MembersWithVerifiedRole}**\n" +
                      $"- Members with only `Unverified`: **{result.MembersWithUnverifiedRole}**\n" +
                      $"- JSON promoted to verified: **{result.Promoted}**\n" +
                      $"- JSON demoted to unverified: **{result.Demoted}**\n" +
                      $"- Conflicting roles (both): **{result.Conflicts}**\n" +
                      $"- Neither status role: **{result.NoStatusRole}**\n" +
                      $"- No longer in server: **{result.MissingMembers}**.{roleWarning}";
        await command.ModifyOriginalResponseAsync(message => message.Content = content);
    }
}
