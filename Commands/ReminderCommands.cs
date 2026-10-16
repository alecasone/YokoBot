using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class ReminderCommands
{
    public static ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName("reminder")
            .WithDescription("Creates a reusable one-shot reminder, or activates it again by name.")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("name")
                .WithDescription("Saved reminder name")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .WithMinLength(1)
                .WithMaxLength(ScheduleRules.MaxNameLength)
                .WithAutocomplete(true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("timespan")
                .WithDescription("Delay for a new reminder, such as 30m, 2h, or 1d (1 minute to 365 days)")
                .WithType(ApplicationCommandOptionType.String)
                .WithMinLength(1)
                .WithMaxLength(64))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("destination")
                .WithDescription("For a new reminder: here, #channel mention, @user mention for a DM, or dm me")
                .WithType(ApplicationCommandOptionType.String)
                .WithMinLength(1)
                .WithMaxLength(100))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("message")
                .WithDescription("Message sent once when the new reminder is due")
                .WithType(ApplicationCommandOptionType.String)
                .WithMinLength(1)
                .WithMaxLength(ScheduleRules.MaxMessageLength))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action")
                .WithDescription("Default: activate; view settings, cancel a pending send, or delete the reminder")
                .WithType(ApplicationCommandOptionType.String)
                .AddChoice("activate", "activate")
                .AddChoice("view", "view")
                .AddChoice("cancel", "cancel")
                .AddChoice("delete", "delete"))
            .Build();

    internal static bool HasConfiguration(SocketSlashCommand command) =>
        command.Data.Options.Any(option => option.Name is "timespan" or "destination" or "message");

    public static async Task HandleAsync(
        SocketSlashCommand command, ScheduledMessageStore store, PermissionService permissions)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);
        var name = Read(command, "name")?.Trim() ?? string.Empty;
        var action = Read(command, "action") ?? "activate";
        var hasConfiguration = HasConfiguration(command);
        var requiresManagement = hasConfiguration || action is "cancel" or "delete";
        var allowed = requiresManagement
            ? await permissions.HasAsync(guildId, command.User, "reminder.manage")
            : await permissions.HasAnyAsync(guildId, command.User, ["reminder.activate", "reminder.manage"]);
        if (!allowed)
        {
            await ReplyAsync(command, requiresManagement
                ? "You need `reminder.manage` to create, cancel, or delete reminders."
                : "You need `reminder.activate` to activate or view reminders.");
            return;
        }

        if (!ScheduleRules.IsValidName(name))
        {
            await ReplyAsync(command, $"Use a reminder name between 1 and {ScheduleRules.MaxNameLength} characters containing only letters, numbers, hyphens, or underscores.");
            return;
        }
        if (action is not ("activate" or "view" or "cancel" or "delete"))
        {
            await ReplyAsync(command, "Choose an action: `activate`, `view`, `cancel`, or `delete`.");
            return;
        }
        if (hasConfiguration && action != "activate")
        {
            await ReplyAsync(command, "Supply `timespan`, `destination`, and `message` only when creating a reminder. Leave them out for view, cancel, or delete.");
            return;
        }

        if (hasConfiguration)
        {
            await CreateAsync(command, store, guildId, name);
            return;
        }

        var reminder = await store.GetReminderAsync(guildId, name);
        if (reminder is null)
        {
            await ReplyAsync(command,
                $"No reminder named `{name}` exists. Create one with `/reminder name:{name} timespan:2h destination:here message:Time to bump!`. Creation also starts its first countdown.");
            return;
        }

        switch (action)
        {
            case "view":
                await ReplyAsync(command, Describe(reminder));
                return;
            case "delete":
                await ReplyAsync(command, await store.DeleteReminderAsync(guildId, reminder.Name)
                    ? $"Reminder `{reminder.Name}` was deleted, including any pending countdown."
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
                        ? $"Cancelled the pending send for `{reminder.Name}`. Run `/reminder name:{reminder.Name}` to start it again."
                        : $"Reminder `{reminder.Name}` has no pending send. Its saved settings are unchanged.");
                return;
            default:
                await ActivateAsync(command, store, guildId, reminder);
                return;
        }
    }

    private static async Task CreateAsync(
        SocketSlashCommand command, ScheduledMessageStore store, ulong guildId, string name)
    {
        var timespan = Read(command, "timespan");
        var destinationText = Read(command, "destination");
        var message = Read(command, "message");
        if (timespan is null || destinationText is null || message is null)
        {
            await ReplyAsync(command, "To create a reminder, supply all three: `timespan`, `destination`, and `message`. To activate an existing reminder, supply only its `name`.");
            return;
        }
        if (!ScheduleRules.TryParseTimespan(timespan, out var delay))
        {
            await ReplyAsync(command, "Use a timespan such as `30m`, `2h`, `1d`, or `1h30m`, between 1 minute and 365 days.");
            return;
        }
        if (string.IsNullOrWhiteSpace(message) || message.Length > ScheduleRules.MaxMessageLength)
        {
            await ReplyAsync(command, "The reminder message must contain between 1 and 2,000 characters.");
            return;
        }
        if (!ScheduledCommandSupport.TryParseDestination(command, destinationText, out var destination, out var error))
        {
            await ReplyAsync(command, error);
            return;
        }

        var reminder = new ReminderSchedule
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Destination = destination,
            Delay = delay,
            Message = message,
            NextDueAt = DateTimeOffset.UtcNow.Add(delay)
        };
        if (!await store.CreateReminderAsync(guildId, reminder))
        {
            await ReplyAsync(command,
                $"A reminder named `{name}` already exists. Run `/reminder name:{name}` to activate it or use `action:view` to inspect it. To replace its settings, delete it first with `action:delete`.");
            return;
        }

        await ReplyAsync(command,
            $"Created and activated reminder `{name}`. It will send once to {ScheduledCommandSupport.DestinationLabel(destination)} {DueLabel(reminder.NextDueAt!.Value)}.\n" +
            $"After it sends, run `/reminder name:{name}` to start another {ScheduleRules.FormatTimespan(delay)} countdown.");
    }

    private static async Task ActivateAsync(
        SocketSlashCommand command, ScheduledMessageStore store, ulong guildId, ReminderSchedule reminder)
    {
        var alreadyPending = false;
        DateTimeOffset due = default;
        var updated = await store.UpdateReminderAsync(guildId, reminder.Id, current =>
        {
            // Check and arm under the store's gate so concurrent activations cannot stack or reset the timer.
            alreadyPending = current.NextDueAt.HasValue;
            current.NextDueAt ??= DateTimeOffset.UtcNow.Add(current.Delay);
            if (!alreadyPending) current.LastError = null;
            due = current.NextDueAt.Value;
        });
        await ReplyAsync(command, !updated
            ? "That reminder changed or was deleted. Run the command again."
            : alreadyPending
                ? $"Reminder `{reminder.Name}` is already pending for {DueLabel(due)}. Its countdown has not changed."
                : $"Activated reminder `{reminder.Name}`. It will send once to {ScheduledCommandSupport.DestinationLabel(reminder.Destination)} {DueLabel(due)}.");
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
            .OrderBy(reminder => reminder.Name, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .Select(reminder => new AutocompleteResult(reminder.Name, reminder.Name)));
    }

    private static string Describe(ReminderSchedule reminder)
    {
        var state = reminder.NextDueAt is { } due
            ? $"Pending: {DueLabel(due)}"
            : $"Idle. Run `/reminder name:{reminder.Name}` to activate it.";
        var lastError = string.IsNullOrWhiteSpace(reminder.LastError)
            ? string.Empty
            : $"\nLast delivery error: {Shorten(reminder.LastError, 250)}";
        return $"**Reminder `{reminder.Name}`**\n" +
               $"Delay: {ScheduleRules.FormatTimespan(reminder.Delay)}\n" +
               $"Destination: {ScheduledCommandSupport.DestinationLabel(reminder.Destination)}\n" +
               $"{state}{lastError}\nMessage:\n{Shorten(reminder.Message, 1200)}";
    }

    private static string DueLabel(DateTimeOffset due) =>
        $"<t:{due.ToUnixTimeSeconds()}:F> (<t:{due.ToUnixTimeSeconds()}:R>)";

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
