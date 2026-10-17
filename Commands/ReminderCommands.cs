using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class ReminderCommands
{
    // Shared authorization errors use this same policy.
    internal const bool EphemeralResponses = false;

    public static ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName("reminder")
            .WithDescription("Sets up and manages the saved reminder used by /remind. Does not start a countdown.")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("name").WithDescription("Saved reminder to set up, edit, inspect, or select for /remind")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .WithMinLength(1).WithMaxLength(ScheduleRules.MaxNameLength).WithAutocomplete(true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("timespan").WithDescription("Saved delay, such as 30m, 2h, or 1d (1 minute to 365 days)")
                .WithType(ApplicationCommandOptionType.String).WithMinLength(1).WithMaxLength(64))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("destination").WithDescription("Saved destination: here, #channel mention, @user mention for a DM, or dm me")
                .WithType(ApplicationCommandOptionType.String).WithMinLength(1).WithMaxLength(100))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("message").WithDescription("Saved message to send once after /remind starts the countdown")
                .WithType(ApplicationCommandOptionType.String).WithMinLength(1).WithMaxLength(ScheduleRules.MaxMessageLength))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action").WithDescription("Default: save supplied settings, or view. Use selects the saved reminder for /remind.")
                .WithType(ApplicationCommandOptionType.String)
                .AddChoice("setup", "setup").AddChoice("use", "use").AddChoice("view", "view")
                .AddChoice("cancel", "cancel").AddChoice("delete", "delete"))
            .Build();

    public static ApplicationCommandProperties BuildRemind() =>
        new SlashCommandBuilder().WithName("remind")
            .WithDescription("Starts the server's saved reminder. No setup or other fields needed.").Build();

    public static async Task HandleAsync(
        SocketSlashCommand command, ScheduledMessageStore store, PermissionService permissions)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: EphemeralResponses);
            return;
        }
        await command.DeferAsync(ephemeral: EphemeralResponses);
        if (!await permissions.HasAsync(guildId, command.User, "reminder.manage"))
        {
            await ReplyAsync(command, "You need `reminder.manage` to set up or manage reminders. Use `/remind` to start the saved reminder.");
            return;
        }

        var name = Read(command, "name")?.Trim() ?? string.Empty;
        var hasConfiguration = command.Data.Options.Any(option => option.Name is "timespan" or "destination" or "message");
        var action = Read(command, "action") ?? (hasConfiguration ? "setup" : "view");
        if (!ScheduleRules.IsValidName(name))
        {
            await ReplyAsync(command, $"Use a reminder name between 1 and {ScheduleRules.MaxNameLength} characters containing only letters, numbers, hyphens, or underscores.");
            return;
        }
        if (action is not ("setup" or "use" or "view" or "cancel" or "delete"))
        {
            await ReplyAsync(command, "Choose `setup`, `use`, `view`, `cancel`, or `delete`. To start the saved countdown, run `/remind` with no fields.");
            return;
        }
        if (hasConfiguration && action != "setup")
        {
            await ReplyAsync(command, "Supply settings only with `action:setup` (or omit action to save them). Leave settings out for use, view, cancel, or delete.");
            return;
        }
        if (action == "setup")
        {
            await ConfigureAsync(command, store, guildId, name);
            return;
        }

        var reminder = await store.GetReminderAsync(guildId, name);
        if (reminder is null)
        {
            await ReplyAsync(command, $"No reminder named `{name}` exists. Set it up with `/reminder name:{name} timespan:2h destination:here message:Time to bump!`, then run `/remind` to start it.");
            return;
        }
        switch (action)
        {
            case "view":
                var selected = await store.GetSelectedReminderAsync(guildId);
                await ReplyAsync(command, Describe(reminder, selected?.Id == reminder.Id));
                return;
            case "use":
                await ReplyAsync(command, await store.SelectReminderAsync(guildId, reminder.Name)
                    ? $"`/remind` now uses `{reminder.Name}`. No countdown was started. Existing pending countdowns are unchanged."
                    : "That reminder was deleted. Set it up again with `/reminder`.");
                return;
            case "delete":
                await ReplyAsync(command, await store.DeleteReminderAsync(guildId, reminder.Name)
                    ? $"Reminder `{reminder.Name}` was deleted, including any pending countdown. If it was selected for `/remind`, set up or select another reminder."
                    : "That reminder was already deleted.");
                return;
            case "cancel":
                var wasPending = false;
                var cancelled = await store.UpdateReminderAsync(guildId, reminder.Id, current =>
                {
                    wasPending = current.NextDueAt.HasValue;
                    current.NextDueAt = null;
                });
                await ReplyAsync(command, !cancelled
                    ? "That reminder changed or was deleted. Run the command again."
                    : wasPending
                        ? $"Cancelled the pending send for `{reminder.Name}`. Its settings are saved. `/remind` starts the selected reminder again."
                        : $"Reminder `{reminder.Name}` has no pending send. Its saved settings are unchanged.");
                return;
        }
    }

    public static async Task HandleRemindAsync(
        SocketSlashCommand command, ScheduledMessageStore store, PermissionService permissions)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: EphemeralResponses);
            return;
        }
        await command.DeferAsync(ephemeral: EphemeralResponses);
        if (!await permissions.HasAnyAsync(guildId, command.User, ["reminder.activate", "reminder.manage"]))
        {
            await ReplyAsync(command, "You need `reminder.activate` to start the saved reminder.");
            return;
        }

        var result = await store.ActivateSelectedReminderAsync(guildId, DateTimeOffset.UtcNow);
        if (result.Reminder is not { } reminder)
        {
            await ReplyAsync(command, "No reminder is selected for this server. A manager needs to set one up with `/reminder`, or select an existing one with `/reminder name:<name> action:use`. Then users with reminder access can run `/remind`.");
            return;
        }
        var due = reminder.NextDueAt!.Value;
        await ReplyAsync(command, result.AlreadyPending
            ? $"Reminder `{reminder.Name}` is already pending for {DueLabel(due)}. Its countdown has not changed."
            : $"Started reminder `{reminder.Name}`. It will send once to {ScheduledCommandSupport.DestinationLabel(reminder.Destination)} {DueLabel(due)}.");
    }

    private static async Task ConfigureAsync(
        SocketSlashCommand command, ScheduledMessageStore store, ulong guildId, string name)
    {
        var timespan = Read(command, "timespan");
        var destinationText = Read(command, "destination");
        var message = Read(command, "message");
        TimeSpan? delay = null;
        ScheduledDestination? destination = null;
        if (timespan is not null)
        {
            if (!ScheduleRules.TryParseTimespan(timespan, out var duration))
            {
                await ReplyAsync(command, "Use a timespan such as `30m`, `2h`, `1d`, or `1h30m`, between 1 minute and 365 days.");
                return;
            }
            delay = duration;
        }
        if (message is not null && (string.IsNullOrWhiteSpace(message) || message.Length > ScheduleRules.MaxMessageLength))
        {
            await ReplyAsync(command, "The reminder message must contain between 1 and 2,000 characters.");
            return;
        }
        if (destinationText is not null)
        {
            if (!ScheduledCommandSupport.TryParseDestination(command, destinationText, out var parsed, out var error))
            {
                await ReplyAsync(command, error);
                return;
            }
            destination = parsed;
        }
        var reminder = await store.ConfigureReminderAsync(guildId, name, delay, destination, message);
        if (reminder is null)
        {
            await ReplyAsync(command, "A new reminder needs `timespan`, `destination`, and `message`. For an existing reminder, supply only the settings you want to change.");
            return;
        }
        await ReplyAsync(command,
            $"Saved reminder `{reminder.Name}` and selected it for `/remind`.\n" +
            $"Delay: {ScheduleRules.FormatTimespan(reminder.Delay)} · Destination: {ScheduledCommandSupport.DestinationLabel(reminder.Destination)}\n" +
            "No countdown is running for this reminder; saving settings cancels its previous countdown. Users can now type just `/remind` to start it.");
    }

    public static async Task HandleAutocompleteAsync(
        SocketAutocompleteInteraction interaction, ScheduledMessageStore store)
    {
        if (interaction.GuildId is not { } guildId || interaction.Data.Current.Name != "name")
        {
            await interaction.RespondAsync([]);
            return;
        }
        var typed = interaction.Data.Current.Value?.ToString() ?? string.Empty;
        var reminders = await store.GetRemindersAsync(guildId);
        await interaction.RespondAsync(reminders
            .Where(reminder => reminder.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(reminder => reminder.Name, StringComparer.OrdinalIgnoreCase).Take(25)
            .Select(reminder => new AutocompleteResult(reminder.Name, reminder.Name)));
    }

    private static string Describe(ReminderSchedule reminder, bool selected)
    {
        var state = reminder.NextDueAt is { } due ? $"Pending: {DueLabel(due)}" : "Idle. No countdown is running.";
        var selection = selected ? "Selected for `/remind`." : $"To use it with `/remind`, run `/reminder name:{reminder.Name} action:use`.";
        var lastError = string.IsNullOrWhiteSpace(reminder.LastError) ? string.Empty : $"\nLast delivery error: {Shorten(reminder.LastError, 250)}";
        return $"**Reminder `{reminder.Name}`**\n{selection}\n" +
               $"Delay: {ScheduleRules.FormatTimespan(reminder.Delay)}\n" +
               $"Destination: {ScheduledCommandSupport.DestinationLabel(reminder.Destination)}\n" +
               $"{state}{lastError}\nMessage:\n{Shorten(reminder.Message, 1200)}";
    }

    private static string DueLabel(DateTimeOffset due) => $"<t:{due.ToUnixTimeSeconds()}:F> (<t:{due.ToUnixTimeSeconds()}:R>)";
    private static string? Read(SocketSlashCommand command, string optionName) =>
        command.Data.Options.FirstOrDefault(option => option.Name == optionName)?.Value?.ToString();
    private static string Shorten(string value, int limit) => value.Length <= limit ? value : value[..(limit - 1)] + "…";
    private static Task ReplyAsync(SocketSlashCommand command, string content) =>
        command.ModifyOriginalResponseAsync(properties =>
        {
            properties.Content = Shorten(content, 1950);
            properties.AllowedMentions = AllowedMentions.None;
        });
}
