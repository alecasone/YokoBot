using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class AutoMessageCommands
{
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong UserId), EditorSlot> Editors = new();
    private static readonly TimeSpan EditorLifetime = TimeSpan.FromMinutes(10);
    private const int MessagesPerPage = 10;
    private const int SchedulesPerPage = 5;

    public static ApplicationCommandProperties Build() => new SlashCommandBuilder()
        .WithName("automessage")
        .WithDescription("Creates and manages recurring messages.")
        .AddOption(Action("create", "Creates a recurring message and opens its message editor.")
            .AddOption(NameOption(autocomplete: false))
            .AddOption("destination", ApplicationCommandOptionType.String,
                "Channel mention, here, user mention for DMs, or dm me", isRequired: true)
            .AddOption("timespan", ApplicationCommandOptionType.String,
                "Time between messages, e.g. 30m, 2h, or 1d (1 minute to 365 days)", isRequired: true)
            .AddOption(ModeOption()))
        .AddOption(Action("edit", "Opens the numbered message list to add, edit, or delete messages.")
            .AddOption(NameOption()))
        .AddOption(Action("mode", "Changes how a recurring message selects its next message.")
            .AddOption(NameOption()).AddOption(ModeOption()))
        .AddOption(Action("pause", "Pauses a recurring message.").AddOption(NameOption()))
        .AddOption(Action("resume", "Resumes a recurring message after its configured interval.").AddOption(NameOption()))
        .AddOption(Action("delete", "Deletes a recurring message and its message list.").AddOption(NameOption()))
        .AddOption(Action("list", "Lists the server's recurring messages and their status.")
            .AddOption(new SlashCommandOptionBuilder().WithName("page").WithDescription("Page number")
                .WithType(ApplicationCommandOptionType.Integer).WithMinValue(1).WithMaxValue(1000000)))
        .Build();

    public static async Task HandleAsync(SocketSlashCommand command, ScheduledMessageStore store)
    {
        if (command.GuildId is not { } guildId || command.Channel is not SocketGuildChannel)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);
        var slot = Editors.GetOrAdd((command.Channel.Id, command.User.Id), _ => new EditorSlot());
        await slot.Gate.WaitAsync();
        try
        {
            var action = command.Data.Options.First();
            if (action.Name == "list")
            {
                var page = Convert.ToInt32(action.Options.FirstOrDefault(option => option.Name == "page")?.Value ?? 1);
                await ReplyAsync(command, FormatSchedules(await store.GetAutoMessagesAsync(guildId), page));
                return;
            }

            var name = Option(action, "name").Trim();
            if (action.Name == "create")
            {
                if (!ScheduleRules.IsValidName(name))
                {
                    await ReplyAsync(command, $"Use a name with 1–{ScheduleRules.MaxNameLength} letters, numbers, hyphens, or underscores.");
                    return;
                }
                if (!ScheduleRules.TryParseTimespan(Option(action, "timespan"), out var interval))
                {
                    await ReplyAsync(command, "Use a timespan from 1 minute to 365 days, such as `30m`, `2h`, `1d`, or `1h30m`.");
                    return;
                }
                var mode = Option(action, "mode").ToLowerInvariant();
                if (!ScheduleRules.IsValidMode(mode))
                {
                    await ReplyAsync(command, "Choose `pseudorandom`, `incremental`, or `random`.");
                    return;
                }
                if (!ScheduledCommandSupport.TryParseDestination(command, Option(action, "destination"), out var destination, out var error))
                {
                    await ReplyAsync(command, error);
                    return;
                }
                var schedule = new AutoMessageSchedule
                {
                    Name = name,
                    Destination = destination,
                    Interval = interval,
                    Mode = mode
                };
                if (!await store.CreateAutoMessageAsync(guildId, schedule))
                {
                    await ReplyAsync(command, $"An automessage named `{name}` already exists. Use `/automessage edit`.");
                    return;
                }
                var editor = new MessageEditor(guildId, schedule.Id, schedule.Name, command);
                slot.Editor = editor;
                await ShowEditorAsync(slot, editor, schedule,
                    "Created. Add your first message to start the timer.");
                return;
            }

            if (action.Name == "delete")
            {
                var deleted = await store.DeleteAutoMessageAsync(guildId, name);
                if (deleted && slot.Editor is { } active && active.GuildId == guildId &&
                    active.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    slot.Editor = null;
                await ReplyAsync(command, deleted ? $"Automessage `{name}` was deleted." : "That automessage does not exist.");
                return;
            }

            var existing = await store.GetAutoMessageAsync(guildId, name);
            if (existing is null)
            {
                await ReplyAsync(command, "That automessage does not exist.");
                return;
            }
            if (action.Name == "edit")
            {
                var editor = new MessageEditor(guildId, existing.Id, existing.Name, command);
                slot.Editor = editor;
                await ShowEditorAsync(slot, editor, existing);
                return;
            }
            if (action.Name == "mode")
            {
                var mode = Option(action, "mode").ToLowerInvariant();
                if (!ScheduleRules.IsValidMode(mode))
                {
                    await ReplyAsync(command, "Choose `pseudorandom`, `incremental`, or `random`.");
                    return;
                }
                var updated = await store.UpdateAutoMessageAsync(guildId, existing.Id, schedule =>
                {
                    schedule.Mode = mode;
                    schedule.NextIndex = 0;
                    schedule.ShuffleBag.Clear();
                });
                await ReplyAsync(command, updated
                    ? $"Automessage `{existing.Name}` now uses `{mode}` mode."
                    : "That automessage changed or was deleted. Run the command again.");
                return;
            }
            if (action.Name is "pause" or "resume")
            {
                var paused = action.Name == "pause";
                var updated = await store.UpdateAutoMessageAsync(guildId, existing.Id, schedule =>
                {
                    if (schedule.Paused == paused) return;
                    schedule.Paused = paused;
                    schedule.NextDueAt = !paused && schedule.Messages.Count > 0
                        ? DateTimeOffset.UtcNow + schedule.Interval : null;
                    if (!paused) schedule.LastError = null;
                });
                await ReplyAsync(command, !updated
                    ? "That automessage changed or was deleted. Run the command again."
                    : paused ? $"Automessage `{existing.Name}` is paused."
                    : existing.Messages.Count == 0 ? $"Automessage `{existing.Name}` is enabled and waiting for its first message."
                    : existing.Paused ? $"Automessage `{existing.Name}` resumed. Its next message is in {ScheduleRules.FormatTimespan(existing.Interval)}."
                    : $"Automessage `{existing.Name}` is already running.");
                return;
            }
            await ReplyAsync(command, "Unknown automessage action.");
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public static async Task<bool> HandleWizardMessageAsync(
        SocketMessage message, ScheduledMessageStore store, PermissionService permissions)
    {
        if (message.Author.IsBot ||
            !Editors.TryGetValue((message.Channel.Id, message.Author.Id), out var slot) || slot.Editor is null)
            return false;

        await slot.Gate.WaitAsync();
        try
        {
            if (slot.Editor is not { } editor) return false;
            if (DateTimeOffset.UtcNow >= editor.ExpiresAt)
            {
                slot.Editor = null;
                await TryUpdateAsync(editor, "This automessage editor expired. Run `/automessage edit` to open it again.");
                return false;
            }
            if (message.Channel is not SocketGuildChannel channel || channel.Guild.Id != editor.GuildId)
                return false;

            await DeleteReplyAsync(message);
            if (!await permissions.HasAnyAsync(editor.GuildId, message.Author, ["automessage.manage"]))
            {
                slot.Editor = null;
                await TryUpdateAsync(editor, "The editor closed because you no longer have `automessage.manage` permission.");
                return true;
            }

            var reply = message.Content.Trim();
            if (reply.Equals("done", StringComparison.OrdinalIgnoreCase) ||
                reply.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                slot.Editor = null;
                await TryUpdateAsync(editor, $"Closed the editor for `{editor.Name}`. All saved changes are kept.");
                return true;
            }

            var schedule = await store.GetAutoMessageAsync(editor.GuildId, editor.Name);
            if (schedule is null || schedule.Id != editor.ScheduleId)
            {
                slot.Editor = null;
                await TryUpdateAsync(editor, "That automessage was deleted or replaced. Run `/automessage edit` again.");
                return true;
            }

            if (!schedule.Messages.SequenceEqual(editor.DisplayedMessages, StringComparer.Ordinal))
            {
                editor.PendingIndex = null;
                await ShowEditorAsync(slot, editor, schedule,
                    "The message list changed in another editor. Review the refreshed list and try again.");
                return true;
            }

            if (editor.PendingIndex is { } pendingIndex)
            {
                if (!IsValidMessage(message.Content))
                {
                    await ShowEditorAsync(slot, editor, schedule,
                        $"The replacement must contain 1–{ScheduleRules.MaxMessageLength} characters and cannot be blank.");
                    return true;
                }
                var saved = await UpdateMessagesAsync(store, editor, current => current.Messages[pendingIndex] = message.Content);
                editor.PendingIndex = null;
                await RefreshEditorAsync(store, slot, editor,
                    saved ? $"Updated message {pendingIndex + 1}." : "The message list changed. Review it and try again.");
                return true;
            }

            var separator = reply.IndexOfAny([' ', '\t', '\r', '\n']);
            var verb = (separator < 0 ? reply : reply[..separator]).ToLowerInvariant();
            var argument = separator < 0 ? string.Empty : reply[(separator + 1)..].TrimStart();
            if (verb == "add")
            {
                if (!IsValidMessage(argument))
                {
                    await ShowEditorAsync(slot, editor, schedule,
                        $"Use `add <message>` with 1–{ScheduleRules.MaxMessageLength} characters; the message cannot be blank.");
                    return true;
                }
                var saved = await UpdateMessagesAsync(store, editor, current => current.Messages.Add(argument));
                if (saved) editor.Page = (schedule.Messages.Count / MessagesPerPage) + 1;
                await RefreshEditorAsync(store, slot, editor,
                    saved ? $"Added message {schedule.Messages.Count + 1}." : "The message list changed. Review it and try again.");
                return true;
            }
            if (verb is "edit" or "delete")
            {
                if (!int.TryParse(argument, out var index) || index < 1 || index > schedule.Messages.Count)
                {
                    await ShowEditorAsync(slot, editor, schedule, "Choose a message number from the list.");
                    return true;
                }
                if (verb == "edit")
                {
                    editor.PendingIndex = index - 1;
                    await ShowEditorAsync(slot, editor, schedule);
                    return true;
                }
                var saved = await UpdateMessagesAsync(store, editor, current => current.Messages.RemoveAt(index - 1));
                await RefreshEditorAsync(store, slot, editor,
                    saved ? $"Deleted message {index}." : "The message list changed. Review it and try again.");
                return true;
            }
            if (verb == "list")
            {
                if (argument.Length > 0 && (!int.TryParse(argument, out var page) || page < 1))
                {
                    await ShowEditorAsync(slot, editor, schedule, "Use `list` or `list <page number>`.");
                    return true;
                }
                if (argument.Length > 0) editor.Page = int.Parse(argument);
                await ShowEditorAsync(slot, editor, schedule);
                return true;
            }
            await ShowEditorAsync(slot, editor, schedule, "Use `add <message>`, `edit <index>`, `delete <index>`, `list <page>`, or `done`.");
            return true;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public static async Task HandleAutocompleteAsync(SocketAutocompleteInteraction interaction, ScheduledMessageStore store)
    {
        if (interaction.GuildId is not { } guildId || interaction.Data.Current.Name != "name")
        {
            await interaction.RespondAsync([]);
            return;
        }
        var typed = interaction.Data.Current.Value?.ToString() ?? string.Empty;
        await interaction.RespondAsync((await store.GetAutoMessagesAsync(guildId))
            .Where(schedule => schedule.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
            .Take(25).Select(schedule => new AutocompleteResult(schedule.Name, schedule.Name)));
    }

    private static async Task<bool> UpdateMessagesAsync(
        ScheduledMessageStore store, MessageEditor editor, Action<AutoMessageSchedule> update)
    {
        var changed = false;
        var found = await store.UpdateAutoMessageAsync(editor.GuildId, editor.ScheduleId, schedule =>
        {
            // A second editor may have changed the numbered list since it was shown.
            if (!schedule.Messages.SequenceEqual(editor.DisplayedMessages, StringComparer.Ordinal)) return;
            update(schedule);
            schedule.NextIndex = 0;
            schedule.ShuffleBag.Clear();
            schedule.LastIndex = null;
            schedule.LastError = null;
            if (schedule.Messages.Count == 0 || schedule.Paused)
                schedule.NextDueAt = null;
            else
                schedule.NextDueAt ??= DateTimeOffset.UtcNow + schedule.Interval;
            changed = true;
        });
        return found && changed;
    }

    private static async Task RefreshEditorAsync(
        ScheduledMessageStore store, EditorSlot slot, MessageEditor editor, string status)
    {
        var schedule = await store.GetAutoMessageAsync(editor.GuildId, editor.Name);
        if (schedule is null || schedule.Id != editor.ScheduleId)
        {
            slot.Editor = null;
            await TryUpdateAsync(editor, "That automessage was deleted or replaced. Run `/automessage edit` again.");
            return;
        }
        await ShowEditorAsync(slot, editor, schedule, status);
    }

    private static async Task ShowEditorAsync(
        EditorSlot slot, MessageEditor editor, AutoMessageSchedule schedule, string? status = null)
    {
        editor.DisplayedMessages = schedule.Messages.ToArray();
        var content = $"**Automessage `{schedule.Name}`** · `{schedule.Mode}` · every {ScheduleRules.FormatTimespan(schedule.Interval)}\n" +
                      $"Destination: {ScheduledCommandSupport.DestinationLabel(schedule.Destination)} · {Status(schedule)}\n";
        if (status is not null) content += $"\n{status}\n";
        if (editor.PendingIndex is { } pendingIndex)
        {
            content += $"\nSend the replacement for **message {pendingIndex + 1}** (up to {ScheduleRules.MaxMessageLength} characters).\n" +
                       $"Current: {Preview(schedule.Messages[pendingIndex], 250)}\n\n" +
                       "Reply `done` or `cancel` to close the editor. Other replies become the replacement message.";
        }
        else
        {
            var pages = Math.Max(1, (schedule.Messages.Count + MessagesPerPage - 1) / MessagesPerPage);
            editor.Page = Math.Clamp(editor.Page, 1, pages);
            content += $"\n**Messages — page {editor.Page}/{pages} ({schedule.Messages.Count} total)**\n";
            if (schedule.Messages.Count == 0) content += "No messages yet. Add one to start the timer.\n";
            for (var index = (editor.Page - 1) * MessagesPerPage;
                 index < Math.Min(editor.Page * MessagesPerPage, schedule.Messages.Count); index++)
                content += $"**{index + 1}.** {Preview(schedule.Messages[index], 100)}\n";
            content += "\nReply `add <message>`, `edit <index>`, `delete <index>`, `list <page>`, or `done`.\n" +
                       "Changes save immediately. This editor expires 10 minutes after opening. Replies are deleted when possible.";
        }
        if (!await TryUpdateAsync(editor, content)) slot.Editor = null;
    }

    private static string FormatSchedules(IReadOnlyList<AutoMessageSchedule> schedules, int page)
    {
        if (schedules.Count == 0) return "No automessages are configured. Use `/automessage create` to add one.";
        var pages = (schedules.Count + SchedulesPerPage - 1) / SchedulesPerPage;
        page = Math.Clamp(page, 1, pages);
        var lines = new List<string> { $"**Automessages — page {page}/{pages}**" };
        foreach (var schedule in schedules.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .Skip((page - 1) * SchedulesPerPage).Take(SchedulesPerPage))
        {
            lines.Add($"`{schedule.Name}` → {ScheduledCommandSupport.DestinationLabel(schedule.Destination)}\n" +
                      $"{ScheduleRules.FormatTimespan(schedule.Interval)} · `{schedule.Mode}` · {schedule.Messages.Count} messages · {Status(schedule)}" +
                      (schedule.LastError is null ? string.Empty : $"\nLast delivery error: {Preview(schedule.LastError, 100)}"));
        }
        if (pages > 1) lines.Add("Use `/automessage list page:<number>` for another page.");
        return string.Join("\n\n", lines);
    }

    private static string Status(AutoMessageSchedule schedule) => schedule.Paused ? "paused"
        : schedule.Messages.Count == 0 ? "waiting for messages"
        : schedule.NextDueAt is { } due ? $"next <t:{due.ToUnixTimeSeconds()}:R>" : "enabled";

    private static string Preview(string value, int length)
    {
        var preview = value.Replace("\r", string.Empty).Replace("\n", " ↵ ");
        return preview.Length > length ? preview[..length] + "…" : preview;
    }

    private static bool IsValidMessage(string message) =>
        !string.IsNullOrWhiteSpace(message) && message.Length <= ScheduleRules.MaxMessageLength;

    private static SlashCommandOptionBuilder Action(string name, string description) => new SlashCommandOptionBuilder()
        .WithName(name).WithDescription(description).WithType(ApplicationCommandOptionType.SubCommand);

    private static SlashCommandOptionBuilder NameOption(bool autocomplete = true) => new SlashCommandOptionBuilder()
        .WithName("name").WithDescription("Automessage name")
        .WithType(ApplicationCommandOptionType.String).WithRequired(true)
        .WithMinLength(1).WithMaxLength(ScheduleRules.MaxNameLength).WithAutocomplete(autocomplete);

    private static SlashCommandOptionBuilder ModeOption() => new SlashCommandOptionBuilder()
        .WithName("mode").WithDescription("Shuffled cycles, ordered cycles, or independent random picks")
        .WithType(ApplicationCommandOptionType.String).WithRequired(true)
        .AddChoice("pseudorandom", "pseudorandom").AddChoice("incremental", "incremental").AddChoice("random", "random");

    private static string Option(SocketSlashCommandDataOption action, string name) =>
        action.Options.First(option => option.Name == name).Value.ToString() ?? string.Empty;

    private static Task ReplyAsync(SocketSlashCommand command, string content) =>
        command.ModifyOriginalResponseAsync(properties =>
        {
            properties.Content = content.Length > 2000 ? content[..1999] + "…" : content;
            properties.AllowedMentions = AllowedMentions.None;
        });

    private static async Task<bool> TryUpdateAsync(MessageEditor editor, string content)
    {
        try
        {
            await ReplyAsync(editor.Interaction, content);
            return true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not update automessage editor: {exception.Message}");
            return false;
        }
    }

    private static async Task DeleteReplyAsync(SocketMessage message)
    {
        try { await message.DeleteAsync(); }
        catch (Exception exception) { Console.WriteLine($"Could not delete automessage editor reply: {exception.Message}"); }
    }

    private sealed class EditorSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public MessageEditor? Editor { get; set; }
    }

    private sealed class MessageEditor(ulong guildId, string scheduleId, string name, SocketSlashCommand interaction)
    {
        public ulong GuildId { get; } = guildId;
        public string ScheduleId { get; } = scheduleId;
        public string Name { get; } = name;
        public SocketSlashCommand Interaction { get; } = interaction;
        public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow + EditorLifetime;
        public string[] DisplayedMessages { get; set; } = [];
        public int Page { get; set; } = 1;
        public int? PendingIndex { get; set; }
    }
}
