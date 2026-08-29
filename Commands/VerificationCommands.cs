using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class VerificationCommands
{
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong AdminId), ProfileWizard> Wizards = new();
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong AdminId), SuccessMessageWizard> SuccessMessageWizards = new();

    public static ApplicationCommandProperties VerifyCommand() =>
        new SlashCommandBuilder()
            .WithName("verify")
            .WithDescription("Verifies a member with a configured role profile.")
            .WithDefaultMemberPermissions(GuildPermission.Administrator)
            .AddOption("user", ApplicationCommandOptionType.User, "Member to verify", isRequired: true)
            .AddOption(TypeOption("type", "Verification type", autocomplete: true))
            .Build();

    public static ApplicationCommandProperties AdminCommand()
    {
        var roles = new SlashCommandOptionBuilder()
            .WithName("role")
            .WithDescription("Adds, edits, or deletes complete verification role profiles.")
            .WithType(ApplicationCommandOptionType.SubCommandGroup)
            .AddOption(ProfileCommand("add", "Creates a role profile through a private wizard.", autocomplete: false))
            .AddOption(ProfileCommand("edit", "Replaces a role profile through a private wizard.", autocomplete: true))
            .AddOption(ProfileCommand("delete", "Deletes a role profile without deleting Discord roles.", autocomplete: true));

        var successMessage = new SlashCommandOptionBuilder()
            .WithName("successmessage")
            .WithDescription("Sets the message posted after successful verification.")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption("channel", ApplicationCommandOptionType.Channel, "Channel that receives the message", isRequired: true);

        return new SlashCommandBuilder()
            .WithName("verifyadmin")
            .WithDescription("Configures verification role profiles.")
            .WithDefaultMemberPermissions(GuildPermission.Administrator)
            .AddOption(roles)
            .AddOption(successMessage)
            .Build();
    }

    public static async Task HandleVerifyAsync(SocketSlashCommand command, VerificationService verification)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var user = (IUser)command.Data.Options.First(option => option.Name == "user").Value;
        var type = (string)command.Data.Options.First(option => option.Name == "type").Value;
        await command.DeferAsync(ephemeral: true);
        var result = await verification.VerifyAsync(guildId, user.Id, type);
        await command.ModifyOriginalResponseAsync(response => response.Content = result);
    }

    public static async Task HandleAdminAsync(SocketSlashCommand command, VerificationSettingsStore store)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var rootOption = command.Data.Options.First();
        if (rootOption.Name == "successmessage")
        {
            var channel = (IChannel)rootOption.Options.First(option => option.Name == "channel").Value;
            if (channel is not ITextChannel)
            {
                await command.RespondAsync("Choose a text channel.", ephemeral: true);
                return;
            }

            Wizards.TryRemove((command.Channel.Id, command.User.Id), out _);
            SuccessMessageWizards[(command.Channel.Id, command.User.Id)] =
                new SuccessMessageWizard(guildId, channel.Id, command);
            await command.RespondAsync(
                $"What message should be posted in <#{channel.Id}> after verification? Reply in this channel with the complete message. " +
                "Markdown, emoji, and mentions are preserved; `{user}` or `@{user}` mentions the verified member. Reply `cancel` to stop. Your reply will be deleted.",
                ephemeral: true);
            return;
        }

        var action = rootOption.Options.First();
        var requestedType = (string)action.Options.First(option => option.Name == "type").Value;
        var type = VerificationSettingsStore.Normalize(requestedType);

        if (action.Name == "delete")
        {
            var deleted = await store.DeleteAsync(guildId, type);
            await command.RespondAsync(deleted
                ? $"Verification profile `{type}` was deleted. No Discord roles were deleted."
                : "Verification profile not found.", ephemeral: true);
            return;
        }

        var settings = await store.GetAsync(guildId);
        var existingEntry = settings.Profiles.FirstOrDefault(item =>
            item.Key.Equals(type, StringComparison.OrdinalIgnoreCase));
        var isNew = action.Name == "add";
        if (isNew)
        {
            var result = await store.CreateAsync(guildId, type);
            if (result != "saved")
            {
                await command.RespondAsync(result == "exists" ? "That profile already exists. Use edit." : "Invalid profile name.", ephemeral: true);
                return;
            }
        }
        else if (existingEntry.Value is null)
        {
            await command.RespondAsync("Verification profile not found.", ephemeral: true);
            return;
        }

        var profile = existingEntry.Value ?? new VerificationProfile();
        var wizard = new ProfileWizard(
            guildId,
            type,
            command,
            isNew,
            [.. profile.AddedRoleIds],
            [.. profile.RemovedRoleIds]);
        SuccessMessageWizards.TryRemove((command.Channel.Id, command.User.Id), out _);
        Wizards[(command.Channel.Id, command.User.Id)] = wizard;
        await command.RespondAsync(AddRolesPrompt(wizard), ephemeral: true);
    }

    public static async Task<bool> HandleWizardMessageAsync(
        SocketMessage message,
        VerificationSettingsStore store)
    {
        if (message.Author.IsBot) return false;

        if (SuccessMessageWizards.TryGetValue((message.Channel.Id, message.Author.Id), out var messageWizard))
        {
            var content = message.Content;
            await DeleteReplyAsync(message);
            if (content.Trim().Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
                content.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase) ||
                content.Trim().Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                SuccessMessageWizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
                await messageWizard.Interaction.ModifyOriginalResponseAsync(properties =>
                    properties.Content = "Success-message setup cancelled.");
                return true;
            }

            await store.SetSuccessMessageAsync(messageWizard.GuildId, messageWizard.DestinationChannelId, content);
            SuccessMessageWizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
            await messageWizard.Interaction.ModifyOriginalResponseAsync(properties =>
                properties.Content = $"Verification success message saved for <#{messageWizard.DestinationChannelId}>.");
            return true;
        }

        if (!Wizards.TryGetValue((message.Channel.Id, message.Author.Id), out var wizard))
            return false;

        var reply = message.Content.Trim();
        await DeleteReplyAsync(message);
        if (reply.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            Wizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
            if (wizard.IsNew) await store.DeleteAsync(wizard.GuildId, wizard.Type);
            await UpdateAsync(wizard, "Verification profile setup cancelled.");
            return true;
        }

        var keep = reply.Equals("keep", StringComparison.OrdinalIgnoreCase);
        var empty = reply.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    reply.Equals("skip", StringComparison.OrdinalIgnoreCase);
        var mentionedRoleIds = message.MentionedRoles.Select(role => role.Id).Distinct().ToArray();
        if (!keep && !empty && mentionedRoleIds.Length == 0)
        {
            await UpdateAsync(wizard, "No roles were recognized. Mention one or more roles, or reply `none`, `keep`, or `cancel`.\n\n" + CurrentPrompt(wizard));
            return true;
        }

        if (wizard.Phase == 0)
        {
            if (!keep) wizard.AddedRoleIds = empty ? [] : mentionedRoleIds;
            wizard.Phase = 1;
            await UpdateAsync(wizard, RemoveRolesPrompt(wizard));
            return true;
        }

        if (!keep) wizard.RemovedRoleIds = empty ? [] : mentionedRoleIds;
        await store.ReplaceAsync(wizard.GuildId, wizard.Type, wizard.AddedRoleIds, wizard.RemovedRoleIds);
        Wizards.TryRemove((message.Channel.Id, message.Author.Id), out _);
        await UpdateAsync(wizard,
            $"Profile **{wizard.Type}** saved. `/verify user:{message.Author.Mention} type:{wizard.Type}` will add " +
            $"{RoleList(wizard.AddedRoleIds.Except(wizard.RemovedRoleIds))} and remove {RoleList(wizard.RemovedRoleIds)}.");
        return true;
    }

    public static async Task HandleAutocompleteAsync(
        SocketAutocompleteInteraction interaction,
        VerificationSettingsStore store)
    {
        if (interaction.GuildId is not { } guildId)
        {
            await interaction.RespondAsync([]);
            return;
        }

        var typed = interaction.Data.Current.Value?.ToString() ?? string.Empty;
        await interaction.RespondAsync((await store.GetTypesAsync(guildId))
            .Where(type => type.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(type => new AutocompleteResult(type, type)));
    }

    private static SlashCommandOptionBuilder ProfileCommand(string name, string description, bool autocomplete) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(TypeOption("type", "Profile used by /verify", autocomplete));

    private static SlashCommandOptionBuilder TypeOption(string name, string description, bool autocomplete) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .WithAutocomplete(autocomplete);

    private static string AddRolesPrompt(ProfileWizard wizard) =>
        $"For `/verify @user {wizard.Type}`, what roles should the user **receive**?\n" +
        $"Mention every role in one reply. Current: {RoleList(wizard.AddedRoleIds)}. Reply `none`, `keep`, or `cancel`. Your reply will be deleted.";

    private static string RemoveRolesPrompt(ProfileWizard wizard) =>
        $"For `/verify @user {wizard.Type}`, what roles should be **removed** from the user?\n" +
        $"Mention every role in one reply. Current: {RoleList(wizard.RemovedRoleIds)}. Reply `none`, `keep`, or `cancel`. Your reply will be deleted.";

    private static string CurrentPrompt(ProfileWizard wizard) =>
        wizard.Phase == 0 ? AddRolesPrompt(wizard) : RemoveRolesPrompt(wizard);

    private static string RoleList(IEnumerable<ulong> roleIds)
    {
        var roles = roleIds.Select(id => $"<@&{id}>").ToArray();
        return roles.Length == 0 ? "none" : string.Join(", ", roles);
    }

    private static Task UpdateAsync(ProfileWizard wizard, string content) =>
        wizard.Interaction.ModifyOriginalResponseAsync(properties => properties.Content = content);

    private static async Task DeleteReplyAsync(SocketMessage message)
    {
        try { await message.DeleteAsync(); }
        catch (Exception exception) { Console.WriteLine($"Could not delete verification setup reply: {exception.Message}"); }
    }

    private sealed record ProfileWizard(
        ulong GuildId,
        string Type,
        SocketSlashCommand Interaction,
        bool IsNew,
        ulong[] OriginalAddedRoleIds,
        ulong[] OriginalRemovedRoleIds)
    {
        public int Phase { get; set; }
        public ulong[] AddedRoleIds { get; set; } = OriginalAddedRoleIds;
        public ulong[] RemovedRoleIds { get; set; } = OriginalRemovedRoleIds;
    }

    private sealed record SuccessMessageWizard(
        ulong GuildId,
        ulong DestinationChannelId,
        SocketSlashCommand Interaction);
}
