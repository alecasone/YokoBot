using Discord;
using Discord.WebSocket;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class OverworldCommands
{
    public static ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName("overworld")
            .WithDescription("Configures the roleplay world's universe data.")
            .WithDefaultMemberPermissions(GuildPermission.Administrator)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("worlddate")
                .WithDescription("Sets the current date in the roleplay world.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("date", ApplicationCommandOptionType.String, "Date as dd-mm-yyyy, ddmmyyyy, or dd/mm/yyyy", isRequired: true))
            .Build();

    public static async Task HandleAsync(SocketSlashCommand command, UniverseStore universes)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var subcommand = command.Data.Options.First();
        var input = (string)subcommand.Options.First(option => option.Name == "date").Value;
        if (!UniverseStore.TryParseWorldDate(input, out var worldDate))
        {
            await command.RespondAsync(
                "I couldn't infer that date. Use `dd-mm-yyyy`, `ddmmyyyy`, or `dd/mm/yyyy`, and make sure it is a real calendar date.",
                ephemeral: true);
            return;
        }

        await universes.SetWorldDateAsync(guildId, worldDate!);
        await command.RespondAsync($"The current world date is now **{worldDate!.Display}**.", ephemeral: true);
    }
}
