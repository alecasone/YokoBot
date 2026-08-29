using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static partial class AutoModerationCommands
{
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong AdminId), RuleWizard> Wizards = new();

    public static ApplicationCommandProperties Build()
    {
        var add = new SlashCommandOptionBuilder()
            .WithName("add")
            .WithDescription("Creates an auto-moderation rule through a private wizard.")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption("title", ApplicationCommandOptionType.String, "Unique rule title", isRequired: true);
        var delete = new SlashCommandOptionBuilder()
            .WithName("delete")
            .WithDescription("Deletes an auto-moderation rule and its pending approvals.")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(TitleOption(required: true));
        var view = new SlashCommandOptionBuilder()
            .WithName("view")
            .WithDescription("Shows one rule or lists all rules.")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(TitleOption(required: false));

        return new SlashCommandBuilder()
            .WithName("automod")
            .WithDescription("Creates and manages automatic moderation rules.")
            .WithDefaultMemberPermissions(GuildPermission.Administrator)
            .AddOption(add)
            .AddOption(delete)
            .AddOption(view)
            .Build();
    }

    public static async Task HandleAsync(SocketSlashCommand command, AutoModerationRuleStore store)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var subcommand = command.Data.Options.First();
        var title = subcommand.Options.FirstOrDefault(option => option.Name == "title")?.Value?.ToString();
        if (subcommand.Name == "delete")
        {
            var deleted = await store.DeleteRuleAsync(guildId, title!);
            await command.RespondAsync(deleted ? $"Rule `{title}` was deleted." : "Rule not found.", ephemeral: true);
            return;
        }

        if (subcommand.Name == "view")
        {
            var rules = (await store.GetAsync(guildId)).Rules;
            if (string.IsNullOrWhiteSpace(title))
            {
                var list = rules.Count == 0 ? "No rules configured." : string.Join("\n", rules.Keys.OrderBy(item => item).Select(item => $"- `{item}`"));
                await command.RespondAsync("**Auto-moderation rules**\n" + list, ephemeral: true);
                return;
            }

            var rule = rules.Values.FirstOrDefault(item => item.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
            await command.RespondAsync(rule is null ? "Rule not found." : Format(rule), ephemeral: true);
            return;
        }

        var normalized = AutoModerationRuleStore.Normalize(title!);
        if ((await store.GetTitlesAsync(guildId)).Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            await command.RespondAsync("That rule already exists. Delete it before recreating it.", ephemeral: true);
            return;
        }

        Wizards[(command.Channel.Id, command.User.Id)] = new RuleWizard(
            guildId,
            command,
            new AutoModerationRule { Title = normalized });
        await command.RespondAsync(
            $"Creating `{normalized}`. What type of moderation is this? Reply `time-warn` (more types can be added later), or `cancel`. Your replies will be deleted.",
            ephemeral: true);
    }

    public static async Task<bool> HandleWizardMessageAsync(SocketMessage message, AutoModerationRuleStore store)
    {
        if (message.Author.IsBot || !Wizards.TryGetValue((message.Channel.Id, message.Author.Id), out var wizard))
            return false;

        var reply = message.Content.Trim();
        await DeleteReplyAsync(message);
        if (IsCancel(reply))
        {
            Wizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
            await UpdateAsync(wizard, "Auto-moderation rule setup cancelled.");
            return true;
        }

        var error = await AdvanceAsync(wizard, reply, store);
        if (error is not null) await UpdateAsync(wizard, error + "\n\n" + Prompt(wizard));
        return true;
    }

    public static async Task HandleAutocompleteAsync(SocketAutocompleteInteraction interaction, AutoModerationRuleStore store)
    {
        if (interaction.GuildId is not { } guildId)
        {
            await interaction.RespondAsync([]);
            return;
        }
        var typed = interaction.Data.Current.Value?.ToString() ?? string.Empty;
        await interaction.RespondAsync((await store.GetTitlesAsync(guildId))
            .Where(title => title.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(title => new AutocompleteResult(title, title)));
    }

    private static async Task<string?> AdvanceAsync(RuleWizard wizard, string reply, AutoModerationRuleStore store)
    {
        switch (wizard.Phase)
        {
            case RulePhase.Type:
                if (!reply.Equals("time-warn", StringComparison.OrdinalIgnoreCase)) return "Currently supported type: `time-warn`.";
                wizard.Rule.Type = "time-warn";
                wizard.Phase = RulePhase.Condition;
                break;
            case RulePhase.Condition:
                var condition = NormalizeChoice(reply);
                if (condition is not ("unverified" or "inactive")) return "Choose `unverified` or `inactive`.";
                wizard.Rule.Condition = condition;
                wizard.Phase = RulePhase.Delay;
                break;
            case RulePhase.Delay:
                if (!TryParseDuration(reply, out var delay) || delay <= TimeSpan.Zero) return "Use a duration such as `30s`, `1h30m`, `2d4s`, or `5d6h`.";
                wizard.Rule.DelayHours = delay.TotalHours;
                wizard.Phase = RulePhase.MessageDestination;
                break;
            case RulePhase.MessageDestination:
                var destination = NormalizeChoice(reply) switch
                {
                    "dmuser" or "dm-user" or "dm" => "dm-user",
                    "channel" or "channel-announce" or "channelannounce" or "announce" => "channel-announce",
                    "none" or "skip" => "none",
                    _ => null
                };
                if (destination is null) return "Choose `dm-user`, `channel-announce`, or `none`.";
                wizard.Rule.MessageDestination = destination;
                wizard.Phase = destination switch
                {
                    "channel-announce" => RulePhase.MessageChannel,
                    "dm-user" => RulePhase.MessageTemplate,
                    _ => RulePhase.Action
                };
                break;
            case RulePhase.MessageChannel:
                if (!TryParseChannel(reply, out var messageChannelId)) return "Mention a text channel, for example `#mod-log`.";
                wizard.Rule.MessageChannelId = messageChannelId;
                wizard.Phase = RulePhase.MessageTemplate;
                break;
            case RulePhase.MessageTemplate:
                wizard.Rule.MessageTemplate = reply;
                wizard.Phase = RulePhase.Action;
                break;
            case RulePhase.Action:
                var action = NormalizeChoice(reply);
                if (action is not ("kick" or "ban" or "none" or "mute")) return "Choose `kick`, `ban`, `mute`, or `none`.";
                wizard.Rule.Action = action;
                wizard.Phase = action switch
                {
                    "mute" => RulePhase.MuteDuration,
                    "none" => RulePhase.Complete,
                    _ => RulePhase.NeedApproval
                };
                break;
            case RulePhase.MuteDuration:
                if (!TryParseDuration(reply, out var muteDuration) || muteDuration < TimeSpan.FromMinutes(1) || muteDuration > TimeSpan.FromDays(28))
                    return "Discord timeouts must be between one minute and 28 days, such as `1d`.";
                wizard.Rule.MuteHours = muteDuration.TotalHours;
                wizard.Phase = RulePhase.NeedApproval;
                break;
            case RulePhase.NeedApproval:
                var approval = NormalizeChoice(reply);
                if (approval is not ("yes" or "no")) return "Choose `yes` or `no`.";
                wizard.Rule.NeedApproval = approval == "yes";
                wizard.Phase = wizard.Rule.NeedApproval ? RulePhase.ApprovalChannel : RulePhase.Complete;
                break;
            case RulePhase.ApprovalChannel:
                if (!TryParseChannel(reply, out var approvalChannelId)) return "Mention the channel where admins should receive approval requests.";
                wizard.Rule.ApprovalChannelId = approvalChannelId;
                wizard.Phase = RulePhase.ApprovalTemplate;
                break;
            case RulePhase.ApprovalTemplate:
                wizard.Rule.ApprovalMessageTemplate = reply.Equals("default", StringComparison.OrdinalIgnoreCase)
                    ? "**{automod}** requests `{action}` for {user}. {message}"
                    : reply;
                wizard.Phase = RulePhase.Complete;
                break;
        }

        if (wizard.Phase == RulePhase.Complete)
        {
            var result = await store.AddRuleAsync(wizard.GuildId, wizard.Rule);
            Wizards.TryRemove((wizard.Interaction.Channel.Id, wizard.Interaction.User.Id), out _);
            await UpdateAsync(wizard, result == "saved" ? $"Rule `{wizard.Rule.Title}` saved.\n\n{Format(wizard.Rule)}" : "That rule already exists.");
        }
        else
        {
            await UpdateAsync(wizard, Prompt(wizard));
        }
        return null;
    }

    private static string Prompt(RuleWizard wizard) => wizard.Phase switch
    {
        RulePhase.Condition => "What clock should this rule watch? Reply `unverified` or `inactive`.",
        RulePhase.Delay => "How long before it triggers? Examples: `30s`, `1h30m`, `2d4s`, `5d6h`.",
        RulePhase.MessageDestination => "Should it send a message? Reply `dm-user`, `channel-announce`, or `none`.",
        RulePhase.MessageChannel => "Mention the text channel where the announcement should be sent.",
        RulePhase.MessageTemplate => "Reply with the complete message. Formatting is preserved. Available placeholders: `{user}`, `{title}`, `{action}`.",
        RulePhase.Action => "What moderation action should trigger? Reply `kick`, `ban`, `mute`, or `none`.",
        RulePhase.MuteDuration => "How long should the Discord timeout last? Example: `1d` (maximum 28 days).",
        RulePhase.NeedApproval => "Does this action need administrator approval? Reply `yes` or `no`.",
        RulePhase.ApprovalChannel => "Mention the channel where the approval request should be posted.",
        RulePhase.ApprovalTemplate => "Reply with the approval message template, or `default`. Placeholders: `{automod}`, `{title}`, `{user}`, `{message}`, `{action}`.",
        _ => "Reply `time-warn`."
    } + " Reply `cancel` at any time. Your reply will be deleted.";

    private static string Format(AutoModerationRule rule)
    {
        var destination = rule.MessageDestination switch
        {
            "dm-user" => "DM to the affected user",
            "channel-announce" when rule.MessageChannelId is { } channelId => $"Channel <#{channelId}>",
            _ => "No user message"
        };
        var userMessage = string.IsNullOrWhiteSpace(rule.MessageTemplate)
            ? "None"
            : Quote(rule.MessageTemplate);
        var approval = rule.NeedApproval
            ? $"Yes — <#{rule.ApprovalChannelId}>\nApproval message:\n{Quote(rule.ApprovalMessageTemplate ?? "Default")}" 
            : "No";

        return $"**{rule.Title}**\n" +
               $"Type: `{rule.Type}`\n" +
               $"Condition: `{rule.Condition}` after `{FormatDuration(TimeSpan.FromHours(rule.DelayHours))}`\n" +
               $"User message destination: {destination}\n" +
               $"User message:\n{userMessage}\n" +
               $"Action: `{rule.Action}`" + (rule.Action == "mute" ? $" for `{FormatDuration(TimeSpan.FromHours(rule.MuteHours ?? 1))}`" : string.Empty) + "\n" +
               $"Approval required: {approval}";
    }

    private static SlashCommandOptionBuilder TitleOption(bool required) =>
        new SlashCommandOptionBuilder()
            .WithName("title")
            .WithDescription("Auto-moderation rule title")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(required)
            .WithAutocomplete(true);

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        var compact = WhitespacePattern().Replace(value.Trim().ToLowerInvariant(), string.Empty);
        var matches = DurationTokenPattern().Matches(compact);
        if (matches.Count == 0 || string.Concat(matches.Select(match => match.Value)) != compact)
        {
            duration = default;
            return false;
        }

        double totalSeconds = 0;
        foreach (Match match in matches)
        {
            if (!double.TryParse(match.Groups[1].Value, out var amount) || amount < 0)
            {
                duration = default;
                return false;
            }
            totalSeconds += match.Groups[2].Value switch
            {
                "w" => amount * 7 * 24 * 60 * 60,
                "d" => amount * 24 * 60 * 60,
                "h" => amount * 60 * 60,
                "m" => amount * 60,
                _ => amount
            };
        }

        try
        {
            duration = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }
        catch (OverflowException)
        {
            duration = default;
            return false;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var parts = new List<string>();
        if (duration.Days > 0) parts.Add($"{duration.Days}d");
        if (duration.Hours > 0) parts.Add($"{duration.Hours}h");
        if (duration.Minutes > 0) parts.Add($"{duration.Minutes}m");
        if (duration.Seconds > 0 || parts.Count == 0) parts.Add($"{duration.Seconds}s");
        return string.Concat(parts);
    }

    private static string Quote(string value)
    {
        var display = value.Length > 800 ? value[..800] + "…" : value;
        return string.Join("\n", display.Split('\n').Select(line => $"> {line.TrimEnd('\r')}"));
    }

    private static bool TryParseChannel(string value, out ulong channelId)
    {
        var match = ChannelPattern().Match(value);
        return ulong.TryParse(match.Success ? match.Groups[1].Value : value.Trim(), out channelId);
    }

    private static string NormalizeChoice(string value) => value.Trim().ToLowerInvariant().Replace('_', '-').Replace(" ", "");
    private static bool IsCancel(string value) => value.Equals("cancel", StringComparison.OrdinalIgnoreCase) || value.Equals("stop", StringComparison.OrdinalIgnoreCase) || value.Equals("end", StringComparison.OrdinalIgnoreCase);
    private static Task UpdateAsync(RuleWizard wizard, string content) => wizard.Interaction.ModifyOriginalResponseAsync(properties => properties.Content = content);
    private static async Task DeleteReplyAsync(SocketMessage message)
    {
        try { await message.DeleteAsync(); }
        catch (Exception exception) { Console.WriteLine($"Could not delete auto-moderation setup reply: {exception.Message}"); }
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)([wdhms])", RegexOptions.IgnoreCase)]
    private static partial Regex DurationTokenPattern();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
    [GeneratedRegex(@"<#(\d+)>")]
    private static partial Regex ChannelPattern();

    private sealed record RuleWizard(ulong GuildId, SocketSlashCommand Interaction, AutoModerationRule Rule)
    {
        public RulePhase Phase { get; set; }
    }

    private enum RulePhase
    {
        Type,
        Condition,
        Delay,
        MessageDestination,
        MessageChannel,
        MessageTemplate,
        Action,
        MuteDuration,
        NeedApproval,
        ApprovalChannel,
        ApprovalTemplate,
        Complete
    }
}
