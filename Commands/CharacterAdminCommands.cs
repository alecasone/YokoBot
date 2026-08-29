using Discord;
using Discord.WebSocket;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class CharacterAdminCommands
{
    public static ApplicationCommandProperties Build()
    {
        var properties = new SlashCommandOptionBuilder()
            .WithName("properties")
            .WithDescription("Views or edits the server's default character structure.")
            .WithType(ApplicationCommandOptionType.SubCommandGroup)
            .AddOption(ActionSubcommand("view", "Shows all default character properties."))
            .AddOption(ActionSubcommand("add", "Adds a default property.", propertyRequired: true))
            .AddOption(ActionSubcommand("remove", "Removes a default property.", propertyRequired: true, propertyAutocomplete: true));

        var autofill = new SlashCommandOptionBuilder()
            .WithName("autofill")
            .WithDescription("Manages suggested values for approval fields.")
            .WithType(ApplicationCommandOptionType.SubCommandGroup)
            .AddOption(AutofillSubcommand("add", "Adds a suggested value; unknown fields become default properties."))
            .AddOption(AutofillSubcommand("remove", "Removes a suggested value."));

        return new SlashCommandBuilder()
            .WithName("charadmin")
            .WithDescription("Configures character management for this server.")
            .WithDefaultMemberPermissions(GuildPermission.Administrator)
            .AddOption(properties)
            .AddOption(autofill)
            .Build();
    }

    public static async Task HandleAsync(SocketSlashCommand command, CharacterSettingsStore store)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var group = command.Data.Options.First();
        var action = group.Options.First();

        if (group.Name == "properties")
        {
            if (action.Name == "view")
            {
                var properties = await store.GetDefaultPropertiesAsync(guildId);
                var list = properties.Count == 0 ? "No default properties are configured." : string.Join("\n", properties.Select(item => $"- `{item}`"));
                await command.RespondAsync($"**Default character properties for this server**\n{list}", ephemeral: true);
                return;
            }

            var property = Value(action.Options, "property");
            var changed = action.Name == "add"
                ? await store.AddPropertyAsync(guildId, property)
                : await store.RemovePropertyAsync(guildId, property);
            await command.RespondAsync(changed
                ? $"Default property `{property}` was {(action.Name == "add" ? "added" : "removed")} for this server. Existing character-specific values were preserved."
                : action.Name == "add" ? "That property already exists or is reserved." : "That default property was not found.", ephemeral: true);
            return;
        }

        var field = Value(action.Options, "field");
        var value = Value(action.Options, "value").Trim();
        var autofillChanged = action.Name == "add"
            ? await store.AddAutofillAsync(guildId, field, value)
            : await store.RemoveAutofillAsync(guildId, field, value);
        await command.RespondAsync(autofillChanged
            ? $"Autofill value `{value}` was {(action.Name == "add" ? "added to" : "removed from")} `{field}`."
            : action.Name == "add" ? "That autofill value already exists." : "That autofill value was not found.", ephemeral: true);
    }

    public static async Task HandleAutocompleteAsync(SocketAutocompleteInteraction interaction, CharacterSettingsStore store)
    {
        if (interaction.GuildId is not { } guildId)
        {
            await interaction.RespondAsync([]);
            return;
        }

        var candidates = await store.GetDefaultPropertiesAsync(guildId);
        var typed = interaction.Data.Current.Value?.ToString() ?? string.Empty;
        await interaction.RespondAsync(candidates
            .Where(item => item.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(item => new AutocompleteResult(item, item)));
    }

    private static SlashCommandOptionBuilder ActionSubcommand(
        string name,
        string description,
        bool propertyRequired = false,
        bool propertyAutocomplete = false)
    {
        var command = new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand);
        if (propertyRequired)
        {
            command.AddOption(new SlashCommandOptionBuilder()
                .WithName("property")
                .WithDescription("Property name, such as eye-color")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .WithAutocomplete(propertyAutocomplete));
        }
        return command;
    }

    private static SlashCommandOptionBuilder AutofillSubcommand(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("field")
                .WithDescription("Default property; a new name is allowed when adding")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .WithAutocomplete(true))
            .AddOption("value", ApplicationCommandOptionType.String, "Suggested value", isRequired: true);

    private static string Value(IReadOnlyCollection<SocketSlashCommandDataOption> options, string name) =>
        (string)options.First(option => option.Name == name).Value;
}
