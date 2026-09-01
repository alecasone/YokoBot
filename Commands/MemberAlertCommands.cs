using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static partial class MemberAlertCommands
{
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong AdminId), AlertWizard> Wizards = new();

    public static ApplicationCommandProperties Build()
    {
        var leave = new SlashCommandOptionBuilder()
            .WithName("leave")
            .WithDescription("Configures alerts when members with selected roles leave.")
            .WithType(ApplicationCommandOptionType.SubCommandGroup)
            .AddOption(LeaveAction("add", "Adds a role-based member-leave alert."))
            .AddOption(LeaveAction("edit", "Reconfigures an existing role-based member-leave alert."))
            .AddOption(LeaveAction("delete", "Deletes a role-based member-leave alert."));

        var newAccount = new SlashCommandOptionBuilder()
            .WithName("newaccount")
            .WithDescription("Configures alerts for newly created Discord accounts.")
            .WithType(ApplicationCommandOptionType.SubCommandGroup)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("set")
                .WithDescription("Alerts when a joining account is younger than the selected age.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("days")
                    .WithDescription("Alert when the account is less than this many days old")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(true)
                    .WithMinValue(1)
                    .WithMaxValue(3650)))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("delete")
                .WithDescription("Deletes the new-account alert.")
                .WithType(ApplicationCommandOptionType.SubCommand));

        return new SlashCommandBuilder()
            .WithName("alertadmin")
            .WithDescription("Configures member join and leave alerts.")
            .AddOption(leave)
            .AddOption(newAccount)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("view")
                .WithDescription("Shows every configured member alert.")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();
    }

    public static async Task HandleAsync(SocketSlashCommand command, MemberAlertStore store)
    {
        if (command.GuildId is not { } guildId || command.Channel is not SocketGuildChannel commandChannel)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var root = command.Data.Options.First();
        if (root.Name == "view")
        {
            await command.RespondAsync(FormatSettings(commandChannel.Guild, await store.GetAsync(guildId)), ephemeral: true);
            return;
        }

        var action = root.Options.First();
        if (root.Name == "leave")
        {
            var role = (IRole)action.Options.First(option => option.Name == "role").Value;
            var existing = (await store.GetAsync(guildId)).LeaveAlerts.GetValueOrDefault(role.Id.ToString());
            if (action.Name == "delete")
            {
                Wizards.TryRemove((command.Channel.Id, command.User.Id), out _);
                var deleted = await store.DeleteLeaveAlertAsync(guildId, role.Id);
                await command.RespondAsync(deleted
                    ? $"The member-leave alert for {role.Mention} was deleted."
                    : $"No member-leave alert is configured for {role.Mention}.", ephemeral: true);
                return;
            }

            if (action.Name == "add" && existing is not null)
            {
                await command.RespondAsync($"An alert already exists for {role.Mention}. Use `/alertadmin leave edit`.", ephemeral: true);
                return;
            }
            if (action.Name == "edit" && existing is null)
            {
                await command.RespondAsync($"No alert exists for {role.Mention}. Use `/alertadmin leave add`.", ephemeral: true);
                return;
            }

            var wizard = new AlertWizard(
                guildId,
                "leave",
                action.Name,
                command,
                role.Id,
                AccountAgeDays: null,
                existing?.Destination,
                existing?.MessageTemplate);
            Wizards[(command.Channel.Id, command.User.Id)] = wizard;
            await command.RespondAsync(DestinationPrompt(wizard), ephemeral: true);
            return;
        }

        if (action.Name == "delete")
        {
            Wizards.TryRemove((command.Channel.Id, command.User.Id), out _);
            var deleted = await store.DeleteNewAccountAlertAsync(guildId);
            await command.RespondAsync(deleted
                ? "The new-account alert was deleted."
                : "No new-account alert is configured.", ephemeral: true);
            return;
        }

        var days = Convert.ToInt32(action.Options.First(option => option.Name == "days").Value);
        var current = (await store.GetAsync(guildId)).NewAccountAlert;
        var accountWizard = new AlertWizard(
            guildId,
            "newaccount",
            "set",
            command,
            RoleId: null,
            days,
            current?.Destination,
            current?.MessageTemplate);
        Wizards[(command.Channel.Id, command.User.Id)] = accountWizard;
        await command.RespondAsync(DestinationPrompt(accountWizard), ephemeral: true);
    }

    public static async Task<bool> HandleWizardMessageAsync(SocketMessage message, MemberAlertStore store)
    {
        if (message.Author.IsBot ||
            !Wizards.TryGetValue((message.Channel.Id, message.Author.Id), out var wizard))
            return false;

        var content = message.Content;
        var reply = content.Trim();
        await DeleteReplyAsync(message);
        if (IsCancel(reply))
        {
            Wizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
            await UpdateAsync(wizard, "Member-alert setup cancelled.");
            return true;
        }

        if (message.Channel is not SocketGuildChannel channel || channel.Guild.Id != wizard.GuildId)
        {
            await UpdateAsync(wizard, "Continue this setup in the server channel where the command was started.");
            return true;
        }

        if (wizard.Phase == AlertWizardPhase.Destination)
        {
            if (reply.Equals("keep", StringComparison.OrdinalIgnoreCase) && wizard.OriginalDestination is not null)
                wizard.Destination = Clone(wizard.OriginalDestination);
            else if (!TryParseDestination(reply, message, wizard, out var destination, out var error))
            {
                await UpdateAsync(wizard, error + "\n\n" + DestinationPrompt(wizard));
                return true;
            }
            else
                wizard.Destination = destination;

            wizard.Phase = AlertWizardPhase.Template;
            await UpdateAsync(wizard, TemplatePrompt(wizard));
            return true;
        }

        var template = reply.Equals("keep", StringComparison.OrdinalIgnoreCase) && wizard.OriginalTemplate is not null
            ? wizard.OriginalTemplate
            : content;
        var saved = await SaveAsync(wizard, template, store);
        Wizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
        await UpdateAsync(wizard, saved
            ? $"Member alert saved.\n\n{FormatWizardResult(wizard, template)}"
            : "The alert changed while setup was open. Run the command again to refresh it.");
        return true;
    }

    private static async Task<bool> SaveAsync(AlertWizard wizard, string template, MemberAlertStore store)
    {
        if (wizard.Destination is null) return false;
        if (wizard.AlertType == "leave" && wizard.RoleId is { } roleId)
        {
            var alert = new RoleLeaveAlert
            {
                RoleId = roleId,
                Destination = Clone(wizard.Destination),
                MessageTemplate = template
            };
            return wizard.Action == "add"
                ? await store.AddLeaveAlertAsync(wizard.GuildId, alert)
                : await store.ReplaceLeaveAlertAsync(wizard.GuildId, alert);
        }

        if (wizard.AlertType == "newaccount" && wizard.AccountAgeDays is { } days)
        {
            await store.SetNewAccountAlertAsync(wizard.GuildId, new NewAccountAlert
            {
                MaximumAccountAgeDays = days,
                Destination = Clone(wizard.Destination),
                MessageTemplate = template
            });
            return true;
        }
        return false;
    }

    private static bool TryParseDestination(
        string reply,
        SocketMessage message,
        AlertWizard wizard,
        out AlertDestination destination,
        out string error)
    {
        destination = new AlertDestination();
        error = string.Empty;
        var normalized = Normalize(reply);
        if (normalized is "here" or "current" or "thischannel")
        {
            destination.Kind = "channel";
            destination.TargetId = message.Channel.Id;
            return true;
        }

        if (normalized is "dm" or "dmsubject" or "subjectdm" or "dmuser")
        {
            destination.Kind = "subject-dm";
            destination.TargetId = null;
            return true;
        }

        if (normalized is "dmme" or "medm")
        {
            destination.Kind = "user-dm";
            destination.TargetId = wizard.Interaction.User.Id;
            return true;
        }

        var channelMatch = ChannelMentionPattern().Match(reply);
        if (channelMatch.Success && ulong.TryParse(channelMatch.Groups[1].Value, out var channelId) &&
            message.Channel is SocketGuildChannel guildChannel &&
            guildChannel.Guild.GetChannel(channelId) is IMessageChannel)
        {
            destination.Kind = "channel";
            destination.TargetId = channelId;
            return true;
        }

        var userMatch = UserMentionPattern().Match(reply);
        if (userMatch.Success && ulong.TryParse(userMatch.Groups[1].Value, out var userId) &&
            message.Channel is SocketGuildChannel memberChannel && memberChannel.Guild.GetUser(userId) is not null)
        {
            destination.Kind = "user-dm";
            destination.TargetId = userId;
            return true;
        }

        error = "I couldn't find that destination. Use `here`, mention `#channel`, reply `dm` for the member who triggers the alert, `dm me`, or `dm @user`.";
        return false;
    }

    private static string DestinationPrompt(AlertWizard wizard)
    {
        var current = wizard.OriginalDestination is null
            ? string.Empty
            : $" Current: {DestinationLabel(wizard.OriginalDestination)}; reply `keep` to retain it.";
        return $"Where should this alert be sent? Reply `here`, mention `#channel`, reply `dm` to DM the member who triggers it, `dm me`, or `dm @user` for a fixed recipient.{current} " +
               "Reply `cancel` to stop. Your reply will be deleted.";
    }

    private static string TemplatePrompt(AlertWizard wizard)
    {
        var eventPlaceholders = wizard.AlertType == "leave"
            ? "`{role}`, `{rolename}`, `{joinedat}`"
            : "`{accountage}`, `{accountagedays}`, `{threshold}`, `{createdat}`";
        var keep = wizard.OriginalTemplate is null ? string.Empty : " Reply `keep` to retain the current message.";
        return $"Reply with the complete alert message for {DestinationLabel(wizard.Destination!)}. Formatting, emoji, and mentions are stored verbatim. " +
               $"Common placeholders: `{{user}}`, `{{username}}`, `{{displayname}}`, `{{userid}}`, `{{server}}`. Event placeholders: {eventPlaceholders}.{keep} " +
               "Reply `cancel` to stop. Your reply will be deleted.";
    }

    private static string FormatSettings(SocketGuild guild, MemberAlertGuildSettings settings)
    {
        var lines = new List<string> { "**Member alerts**" };
        if (settings.LeaveAlerts.Count == 0)
            lines.Add("\n**Role leave alerts**\nNone configured.");
        else
        {
            lines.Add("\n**Role leave alerts**");
            foreach (var alert in settings.LeaveAlerts.Values.OrderBy(alert => guild.GetRole(alert.RoleId)?.Name ?? alert.RoleId.ToString()))
            {
                var role = guild.GetRole(alert.RoleId);
                lines.Add($"- {(role?.Mention ?? $"Deleted role `{alert.RoleId}`")} → {DestinationLabel(alert.Destination)}\n{Quote(alert.MessageTemplate)}");
            }
        }

        var account = settings.NewAccountAlert;
        lines.Add(account is null
            ? "\n**New-account alert**\nNone configured."
            : $"\n**New-account alert**\nLess than **{account.MaximumAccountAgeDays} days** → {DestinationLabel(account.Destination)}\n{Quote(account.MessageTemplate)}");
        var result = string.Join("\n", lines);
        return result.Length <= 1950 ? result : result[..1900] + "\n…Some alert messages were shortened for display.";
    }

    private static string FormatWizardResult(AlertWizard wizard, string template)
    {
        var condition = wizard.AlertType == "leave"
            ? $"Member had <@&{wizard.RoleId}> when leaving"
            : $"Joining account is less than **{wizard.AccountAgeDays} days** old";
        return $"Condition: {condition}\nDestination: {DestinationLabel(wizard.Destination!)}\nMessage:\n{Quote(template)}";
    }

    private static string DestinationLabel(AlertDestination destination) => destination.Kind switch
    {
        "channel" when destination.TargetId is { } channelId => $"<#{channelId}>",
        "subject-dm" => "triggering member's DMs",
        "user-dm" when destination.TargetId is { } userId => $"<@{userId}>'s DMs",
        _ => "unavailable destination"
    };

    private static SlashCommandOptionBuilder LeaveAction(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption("role", ApplicationCommandOptionType.Role, "Role watched by this alert", isRequired: true);

    private static string Quote(string value)
    {
        var display = value.Length > 500 ? value[..500] + "…" : value;
        return string.Join("\n", display.Split('\n').Select(line => $"> {line.TrimEnd('\r')}"));
    }

    private static AlertDestination Clone(AlertDestination destination) => new()
    {
        Kind = destination.Kind,
        TargetId = destination.TargetId
    };

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);

    private static bool IsCancel(string value) =>
        value.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("end", StringComparison.OrdinalIgnoreCase);

    private static Task UpdateAsync(AlertWizard wizard, string content) =>
        wizard.Interaction.ModifyOriginalResponseAsync(properties => properties.Content = content);

    private static async Task DeleteReplyAsync(SocketMessage message)
    {
        try { await message.DeleteAsync(); }
        catch (Exception exception) { Console.WriteLine($"Could not delete member-alert setup reply: {exception.Message}"); }
    }

    [GeneratedRegex(@"<#(\d+)>")]
    private static partial Regex ChannelMentionPattern();

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex UserMentionPattern();

    private sealed record AlertWizard(
        ulong GuildId,
        string AlertType,
        string Action,
        SocketSlashCommand Interaction,
        ulong? RoleId,
        int? AccountAgeDays,
        AlertDestination? OriginalDestination,
        string? OriginalTemplate)
    {
        public AlertWizardPhase Phase { get; set; }
        public AlertDestination? Destination { get; set; }
    }

    private enum AlertWizardPhase
    {
        Destination,
        Template
    }
}
