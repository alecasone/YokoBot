using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class CharacterAdminCommands
{
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong AdminId), RoleWizard> RoleWizards = new();
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong AdminId), ApprovalMessageWizard> ApprovalMessageWizards = new();
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong AdminId), ApprovalMessageDeleteWizard> ApprovalMessageDeleteWizards = new();

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

        var roles = new SlashCommandOptionBuilder()
            .WithName("roles")
            .WithDescription("Configures roles added or removed during character approval.")
            .WithType(ApplicationCommandOptionType.SubCommandGroup)
            .AddOption(ActionSubcommand("add", "Sets default (*) roles and the sequential OC-role ladder."))
            .AddOption(ActionSubcommand("remove", "Sets fixed roles removed whenever a character is approved."));

        var approvalMessage = new SlashCommandOptionBuilder()
            .WithName("approvemessage")
            .WithDescription("Adds or deletes messages sent after character approval.")
            .WithType(ApplicationCommandOptionType.SubCommandGroup)
            .AddOption(ActionSubcommand("add", "Adds one or more character approval messages."))
            .AddOption(ActionSubcommand("delete", "Deletes selected character approval messages."));

        return new SlashCommandBuilder()
            .WithName("charadmin")
            .WithDescription("Configures character management for this server.")
            .AddOption(properties)
            .AddOption(autofill)
            .AddOption(roles)
            .AddOption(approvalMessage)
            .Build();
    }

    public static async Task HandleAsync(
        SocketSlashCommand command,
        CharacterSettingsStore store,
        CharacterRoleService characterRoles)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var group = command.Data.Options.First();
        if (group.Name == "approvemessage")
        {
            RoleWizards.TryRemove((command.Channel.Id, command.User.Id), out _);
            ApprovalMessageWizards.TryRemove((command.Channel.Id, command.User.Id), out _);
            ApprovalMessageDeleteWizards.TryRemove((command.Channel.Id, command.User.Id), out _);
            var approvalAction = group.Options.First();
            var messages = await store.GetApprovalMessagesAsync(guildId);
            if (approvalAction.Name == "delete")
            {
                if (messages.Count == 0)
                {
                    await command.RespondAsync("No character approval messages are configured.", ephemeral: true);
                    return;
                }

                var display = ApprovalMessageDeletePrompt(messages);
                var wizard = new ApprovalMessageDeleteWizard(guildId, command, display.HighestDisplayedIndex);
                ApprovalMessageDeleteWizards[(command.Channel.Id, command.User.Id)] = wizard;
                await command.RespondAsync(display.Content, ephemeral: true);
                return;
            }

            var addWizard = new ApprovalMessageWizard(guildId, command, messages.Count);
            ApprovalMessageWizards[(command.Channel.Id, command.User.Id)] = addWizard;
            await command.RespondAsync(ApprovalDestinationPrompt(addWizard), ephemeral: true);
            return;
        }

        var action = group.Options.First();

        if (group.Name == "roles")
        {
            ApprovalMessageWizards.TryRemove((command.Channel.Id, command.User.Id), out _);
            ApprovalMessageDeleteWizards.TryRemove((command.Channel.Id, command.User.Id), out _);
            var current = await store.GetOcRoleConfigurationAsync(guildId);
            RoleWizards[(command.Channel.Id, command.User.Id)] =
                new RoleWizard(
                    guildId,
                    action.Name,
                    command,
                    current.DefaultRoleIds.ToArray(),
                    current.SequentialRoleIds.ToArray(),
                    current.RemovedRoleIds.ToArray());
            await command.RespondAsync(RolePrompt(action.Name, current), ephemeral: true);
            return;
        }

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

    public static async Task<bool> HandleWizardMessageAsync(
        SocketMessage message,
        CharacterSettingsStore store,
        CharacterRoleService characterRoles)
    {
        if (message.Author.IsBot) return false;

        if (ApprovalMessageDeleteWizards.TryGetValue((message.Channel.Id, message.Author.Id), out var deleteWizard))
        {
            await HandleApprovalMessageDeleteWizardAsync(message, store, deleteWizard);
            return true;
        }

        if (ApprovalMessageWizards.TryGetValue((message.Channel.Id, message.Author.Id), out var approvalWizard))
        {
            await HandleApprovalMessageWizardAsync(message, store, approvalWizard);
            return true;
        }

        if (!RoleWizards.TryGetValue((message.Channel.Id, message.Author.Id), out var wizard)) return false;

        var reply = message.Content.Trim();
        await DeleteReplyAsync(message);
        if (reply.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            RoleWizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
            await UpdateAsync(wizard, "OC-role setup cancelled.");
            return true;
        }

        if (message.Channel is not SocketGuildChannel channel)
        {
            await UpdateAsync(wizard, "This setup must be completed in its original server channel.");
            return true;
        }

        var clear = reply.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    reply.Equals("skip", StringComparison.OrdinalIgnoreCase);
        var roleMentions = Regex.Matches(message.Content, @"(?<default>\*)?[ \t]*<@&(?<id>\d+)>")
            .Select(match => new RoleMention(
                ulong.Parse(match.Groups["id"].Value),
                match.Groups["default"].Success))
            .Where(mention => channel.Guild.GetRole(mention.RoleId) is { IsManaged: false } role &&
                              role.Id != channel.Guild.EveryoneRole.Id)
            .ToArray();
        if (!clear && roleMentions.Length == 0)
        {
            await UpdateAsync(wizard,
                "No manageable roles were recognized. Mention one or more ordinary server roles, reply `none` to clear this list, or reply `cancel`.\n\n" +
                RolePrompt(wizard.Action, wizard.Configuration));
            return true;
        }

        CharacterRoleConfigurationResult result;
        if (wizard.Action == "add")
        {
            var defaultIds = clear
                ? []
                : roleMentions.Where(mention => mention.IsDefault).Select(mention => mention.RoleId).Distinct().ToArray();
            var defaultSet = defaultIds.ToHashSet();
            var sequentialIds = clear
                ? []
                : roleMentions.Where(mention => !defaultSet.Contains(mention.RoleId))
                    .Select(mention => mention.RoleId)
                    .Distinct()
                    .ToArray();
            result = await characterRoles.ConfigureAddedRolesAsync(wizard.GuildId, defaultIds, sequentialIds);
        }
        else
        {
            var removedIds = clear ? [] : roleMentions.Select(mention => mention.RoleId).Distinct().ToArray();
            result = await characterRoles.ConfigureRemovedRolesAsync(wizard.GuildId, removedIds);
        }
        RoleWizards.TryRemove((message.Channel.Id, message.Author.Id), out _);

        var outcome = result.Changed
            ? "OC approval-role configuration saved."
            : "The OC approval-role configuration was already unchanged.";
        var failures = result.MembersFailed == 0
            ? string.Empty
            : $" **{result.MembersFailed}** member(s) could not be synchronized; check Yoko's role hierarchy and Manage Roles permission.";
        await UpdateAsync(wizard,
            $"{outcome} Synchronized **{result.MembersSynced}** member(s).{failures}\n\n" +
            FormatRoleConfiguration(result.DefaultRoleIds, result.SequentialRoleIds, result.RemovedRoleIds));
        return true;
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

    private static string RolePrompt(string action, CharacterRoleConfiguration current) =>
        action == "add"
            ? "Send the complete **roles to add on character approval** in one reply. Prefix always-added default roles with `*`; " +
              "all unstarred roles become the numbered OC ladder in exact left-to-right order. Example: `* @Member @1st-OC @2nd-OC @3rd-OC`. " +
              "Reply `none` to clear the add configuration or `cancel` to stop. Your reply will be deleted.\n\n" +
              FormatRoleConfiguration(current.DefaultRoleIds, current.SequentialRoleIds, current.RemovedRoleIds)
            : "Mention the complete list of fixed roles that should be **removed from a member on character approval**, such as `@No OC`. " +
              "This does not remove numbered roles from the ladder. Reply `none` to clear the removal list or `cancel` to stop. " +
              "Your reply will be deleted.\n\n" +
              FormatRoleConfiguration(current.DefaultRoleIds, current.SequentialRoleIds, current.RemovedRoleIds);

    private static string FormatRoleConfiguration(
        IEnumerable<ulong> defaultRoleIds,
        IEnumerable<ulong> sequentialRoleIds,
        IEnumerable<ulong> removedRoleIds)
    {
        var defaults = defaultRoleIds.Select(roleId => $"\\* <@&{roleId}>").ToArray();
        var sequential = sequentialRoleIds.Select((roleId, index) => $"{index + 1}. <@&{roleId}>").ToArray();
        var removed = removedRoleIds.Select(roleId => $"- <@&{roleId}>").ToArray();
        return "**Always added (`*`)**\n" +
               (defaults.Length == 0 ? "None" : string.Join("\n", defaults)) +
               $"\n\n**Sequential OC roles (capacity {sequential.Length})**\n" +
               (sequential.Length == 0 ? "None; approvals are uncapped." : string.Join("\n", sequential)) +
               "\n\n**Removed on approval**\n" +
               (removed.Length == 0 ? "None" : string.Join("\n", removed));
    }

    private static Task UpdateAsync(RoleWizard wizard, string content) =>
        wizard.Interaction.ModifyOriginalResponseAsync(properties => properties.Content = content);

    private static async Task HandleApprovalMessageWizardAsync(
        SocketMessage message,
        CharacterSettingsStore store,
        ApprovalMessageWizard wizard)
    {
        var content = message.Content;
        var reply = content.Trim();
        await DeleteReplyAsync(message);

        if (reply.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            ApprovalMessageWizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
            var retained = wizard.AddedThisSession == 0
                ? string.Empty
                : $" The **{wizard.AddedThisSession}** message(s) already added during this setup were retained.";
            await UpdateApprovalAsync(wizard, $"Character approval-message setup cancelled.{retained}");
            return;
        }

        if (message.Channel is not SocketGuildChannel channel || channel.Guild.Id != wizard.GuildId)
        {
            await UpdateApprovalAsync(wizard, "Continue this setup in the server channel where the command was started.");
            return;
        }

        if (wizard.Phase == ApprovalMessagePhase.Destination)
        {
            if (reply.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                reply.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                var removed = await store.ClearApprovalMessagesAsync(wizard.GuildId);
                ApprovalMessageWizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
                await UpdateApprovalAsync(wizard, $"Cleared **{removed}** configured character approval message(s).");
                return;
            }

            if (reply.Equals("dm", StringComparison.OrdinalIgnoreCase) ||
                reply.Equals("direct message", StringComparison.OrdinalIgnoreCase))
            {
                wizard.Destination = "dm";
                wizard.ChannelId = null;
            }
            else if (reply.Equals("here", StringComparison.OrdinalIgnoreCase) ||
                     reply.Equals("current", StringComparison.OrdinalIgnoreCase) ||
                     reply.Equals("this channel", StringComparison.OrdinalIgnoreCase))
            {
                wizard.Destination = "here";
                wizard.ChannelId = null;
            }
            else
            {
                var mentionedChannelId = Regex.Match(content, @"<#(?<id>\d+)>") is { Success: true } match
                    ? ulong.Parse(match.Groups["id"].Value)
                    : (ulong?)null;
                var destination = mentionedChannelId is { } channelId
                        ? channel.Guild.GetChannel(channelId) as IMessageChannel
                        : null;
                if (destination is null)
                {
                    await UpdateApprovalAsync(wizard,
                        "I couldn't find that destination. Reply `dm`, `here`, or mention a text channel such as `#general`. " +
                        "Reply `clear` to remove all configured approval messages, or `cancel` to stop.");
                    return;
                }
                wizard.Destination = "channel";
                wizard.ChannelId = destination.Id;
            }

            wizard.Phase = ApprovalMessagePhase.Template;
            await UpdateApprovalAsync(wizard,
                $"What message should be sent to {DestinationLabel(wizard)}? Your next reply is stored verbatim, including Markdown and emoji. " +
                "Use `{user}` for the member and `{charactername}` for the approved character. Reply `cancel` to stop. Your reply will be deleted.");
            return;
        }

        if (wizard.Phase == ApprovalMessagePhase.Template)
        {
            await store.AddApprovalMessageAsync(wizard.GuildId, new CharacterApprovalMessage
            {
                Destination = wizard.Destination,
                ChannelId = wizard.ChannelId,
                Template = content
            });
            wizard.AddedThisSession++;
            wizard.Phase = ApprovalMessagePhase.Another;
            await UpdateApprovalAsync(wizard,
                $"Approval message **{wizard.ExistingCount + wizard.AddedThisSession}** saved for {DestinationLabel(wizard)}. " +
                "Would you like to add another message? Reply `yes` or `no`. Your reply will be deleted.");
            return;
        }

        if (reply.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            wizard.Phase = ApprovalMessagePhase.Destination;
            wizard.Destination = string.Empty;
            wizard.ChannelId = null;
            await UpdateApprovalAsync(wizard, ApprovalDestinationPrompt(wizard));
            return;
        }

        if (reply.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("n", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("done", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            ApprovalMessageWizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
            await UpdateApprovalAsync(wizard,
                $"Character approval-message setup complete. Added **{wizard.AddedThisSession}** message(s); " +
                $"**{wizard.ExistingCount + wizard.AddedThisSession}** total are configured.");
            return;
        }

        await UpdateApprovalAsync(wizard, "Please reply `yes` to configure another approval message or `no` to finish.");
    }

    private static async Task HandleApprovalMessageDeleteWizardAsync(
        SocketMessage message,
        CharacterSettingsStore store,
        ApprovalMessageDeleteWizard wizard)
    {
        var reply = message.Content.Trim();
        await DeleteReplyAsync(message);

        if (reply.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            ApprovalMessageDeleteWizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
            await UpdateApprovalDeleteAsync(wizard, "Character approval-message deletion cancelled.");
            return;
        }

        var parts = reply.Split(',', StringSplitOptions.TrimEntries);
        var indexes = new List<int>();
        var valid = parts.Length > 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var index) || index < 1 || index > wizard.HighestDisplayedIndex)
            {
                valid = false;
                break;
            }
            if (!indexes.Contains(index)) indexes.Add(index);
        }

        if (!valid || indexes.Count == 0)
        {
            await UpdateApprovalDeleteAsync(wizard,
                $"I couldn't understand that selection. Reply with displayed message numbers separated by commas, such as `1`, `1,3`, or `1,4,5` " +
                $"(valid range: 1–{wizard.HighestDisplayedIndex}), or reply `cancel`.");
            return;
        }

        var removed = await store.RemoveApprovalMessagesAsync(wizard.GuildId, indexes);
        var remaining = (await store.GetApprovalMessagesAsync(wizard.GuildId)).Count;
        ApprovalMessageDeleteWizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
        await UpdateApprovalDeleteAsync(wizard,
            $"Deleted **{removed}** character approval message(s). **{remaining}** remain configured.");
    }

    private static ApprovalMessageDeleteDisplay ApprovalMessageDeletePrompt(
        IReadOnlyList<CharacterApprovalMessage> messages)
    {
        const string instructions =
            "**Configured character approval messages**\n" +
            "Reply with message numbers separated by commas—for example `1`, `1,3`, or `1,4,5`. Reply `cancel` to stop. " +
            "Your reply will be deleted.\n\n";
        var content = instructions;
        var displayed = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            var configured = messages[index];
            var destination = configured.Destination.Equals("dm", StringComparison.OrdinalIgnoreCase)
                ? "DM"
                : configured.Destination.Equals("here", StringComparison.OrdinalIgnoreCase)
                    ? "where `/character approve` is used"
                : configured.ChannelId is { } channelId ? $"<#{channelId}>" : "missing channel";
            var preview = configured.Template
                .Replace("\r", string.Empty)
                .Replace("\n", " ↵ ")
                .Replace("`", "ˋ");
            if (preview.Length > 120) preview = preview[..117] + "...";
            if (preview.Length == 0) preview = "(empty message)";
            var line = $"**{index + 1}.** {destination} — `{preview}`\n";
            if (content.Length + line.Length > 1850) break;
            content += line;
            displayed = index + 1;
        }

        if (displayed < messages.Count)
            content += $"\nOnly the first **{displayed}** of **{messages.Count}** messages fit here. Delete from this page, then run the command again for the remainder.";
        return new ApprovalMessageDeleteDisplay(content, displayed);
    }

    private static string ApprovalDestinationPrompt(ApprovalMessageWizard wizard) =>
        $"Where should approval message **{wizard.ExistingCount + wizard.AddedThisSession + 1}** be sent? " +
        "Reply `dm`, `here` to follow the channel where `/character approve` is used, or mention a fixed text channel such as `#general`. " +
        "Reply `clear` to remove all configured approval messages, " +
        "or `cancel` to stop. Your reply will be deleted.";

    private static string DestinationLabel(ApprovalMessageWizard wizard) =>
        wizard.Destination switch
        {
            "dm" => "the approved member's DMs",
            "here" => "the channel where `/character approve` is used",
            _ => $"<#{wizard.ChannelId}>"
        };

    private static Task UpdateApprovalAsync(ApprovalMessageWizard wizard, string content) =>
        wizard.Interaction.ModifyOriginalResponseAsync(properties => properties.Content = content);

    private static Task UpdateApprovalDeleteAsync(ApprovalMessageDeleteWizard wizard, string content) =>
        wizard.Interaction.ModifyOriginalResponseAsync(properties => properties.Content = content);

    private static async Task DeleteReplyAsync(SocketMessage message)
    {
        try { await message.DeleteAsync(); }
        catch (Exception exception) { Console.WriteLine($"Could not delete character-admin setup reply: {exception.Message}"); }
    }

    private sealed record RoleWizard(
        ulong GuildId,
        string Action,
        SocketSlashCommand Interaction,
        ulong[] DefaultRoleIds,
        ulong[] SequentialRoleIds,
        ulong[] RemovedRoleIds)
    {
        public CharacterRoleConfiguration Configuration { get; } =
            new(DefaultRoleIds, SequentialRoleIds, RemovedRoleIds);
    }

    private sealed record RoleMention(ulong RoleId, bool IsDefault);

    private sealed record ApprovalMessageWizard(
        ulong GuildId,
        SocketSlashCommand Interaction,
        int ExistingCount)
    {
        public ApprovalMessagePhase Phase { get; set; }
        public string Destination { get; set; } = string.Empty;
        public ulong? ChannelId { get; set; }
        public int AddedThisSession { get; set; }
    }

    private sealed record ApprovalMessageDeleteWizard(
        ulong GuildId,
        SocketSlashCommand Interaction,
        int HighestDisplayedIndex);

    private sealed record ApprovalMessageDeleteDisplay(string Content, int HighestDisplayedIndex);

    private enum ApprovalMessagePhase
    {
        Destination,
        Template,
        Another
    }
}
