using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class SceneTrackerCommands
{
    public static ApplicationCommandProperties Build()
    {
        var createScene = new SlashCommandOptionBuilder()
            .WithName("create")
            .WithDescription("Creates a scene with one of your characters.")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(CharacterOption("character", "Your character"))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("day")
                .WithDescription("Day within the current world month and year")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(true)
                .WithMinValue(1)
                .WithMaxValue(31))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("title")
                .WithDescription("Optional title; defaults to Scene # — world date")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)
                .WithMaxLength(100));

        var addParticipant = new SlashCommandOptionBuilder()
            .WithName("invite")
            .WithDescription("Invites a member's character to an active scene.")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(SceneOption())
            .AddOption("user", ApplicationCommandOptionType.User, "Character owner", isRequired: true)
            .AddOption(CharacterOption("character", "Character to add"));

        var edit = new SlashCommandOptionBuilder()
            .WithName("edit")
            .WithDescription("Removes characters or members from active scenes.")
            .WithType(ApplicationCommandOptionType.SubCommandGroup)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove-character")
                .WithDescription("Removes one character from a scene.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(SceneOption())
                .AddOption("user", ApplicationCommandOptionType.User, "Character owner", isRequired: true)
                .AddOption(CharacterOption("character", "Character to remove")))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove-user")
                .WithDescription("Removes a member and all of their characters from a scene.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(SceneOption())
                .AddOption("user", ApplicationCommandOptionType.User, "Member to remove", isRequired: true));

        var tracker = new SlashCommandBuilder()
            .WithName("scenetracker")
            .WithDescription("Manages current and past roleplay scenes.")
            .AddOption(createScene)
            .AddOption(addParticipant)
            .AddOption(SceneAction("view", "Shows a scene's information publicly."))
            .AddOption(SceneAction("complete", "Marks an active scene as completed."))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("history")
                .WithDescription("Shows all active and completed scenes.")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(SceneAction("delete", "Permanently deletes an active scene."))
            .AddOption(edit)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("settings")
                .WithDescription("View or configure scene limits, reply name, and acceptance message.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder().WithName("limit").WithDescription("Ongoing scenes per character (default 4)")
                    .WithType(ApplicationCommandOptionType.Integer).WithMinValue(0).WithMaxValue(1000))
                .AddOption(new SlashCommandOptionBuilder().WithName("reply-name").WithDescription("Name in Accept, Helios. Defaults to the bot's server nickname")
                    .WithType(ApplicationCommandOptionType.String).WithMinLength(1).WithMaxLength(32))
                .AddOption(new SlashCommandOptionBuilder().WithName("acceptance-message")
                    .WithDescription("Message after accepting: {user}, {charactername}, {scene}, {date}")
                    .WithType(ApplicationCommandOptionType.String).WithMinLength(1).WithMaxLength(1500)))
            .AddOption(new SlashCommandOptionBuilder().WithName("slots")
                .WithDescription("Manage a member's extra ongoing-scene slots per character.")
                .WithType(ApplicationCommandOptionType.SubCommandGroup)
                .AddOption(SlotAction("add", true)).AddOption(SlotAction("remove", true))
                .AddOption(SlotAction("reset", false)).AddOption(SlotAction("view", false)))
            .Build();

        return tracker;
    }

    public static async Task HandleCreateAsync(
        SocketSlashCommand command,
        UniverseStore universes,
        SceneStore scenes,
        CharacterStore characters)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("Scene commands can only be used in a server.", ephemeral: true);
            return;
        }

        var options = command.Data.Options.First().Options;
        var characterInput = (string)Option(options, "character").Value;
        var character = await characters.GetAsync(guildId, command.User.Id, characterInput);
        if (character is null)
        {
            await command.RespondAsync("That character is not stored under your account.", ephemeral: true);
            return;
        }
        var characterName = character.Name;

        var universe = await universes.GetAsync(guildId);
        if (universe.CurrentWorldDate is not { } currentWorldDate)
        {
            await command.RespondAsync("An administrator must set `/overworld worlddate` before scenes can be created.", ephemeral: true);
            return;
        }

        var day = Convert.ToInt32(Option(options, "day").Value);
        if (!currentWorldDate.IsValidDay(day))
        {
            await command.RespondAsync(
                $"Day **{day}** is not valid in world month **{currentWorldDate.Month:00}-{currentWorldDate.Year:0000}**.",
                ephemeral: true);
            return;
        }

        var sceneDate = currentWorldDate.WithDay(day);
        var requestedTitle = OptionalString(options, "title")?.Trim();
        await command.DeferAsync(ephemeral: true);
        var result = await scenes.CreateAsync(guildId, command.User.Id, character, sceneDate, requestedTitle);
        if (result.Scene is not { } scene)
        {
            await command.ModifyOriginalResponseAsync(p => p.Content = CapacityMessage(result.Capacity));
            return;
        }
        await command.ModifyOriginalResponseAsync(p => p.Content =
            $"Created scene **{scene.Title}** on **{scene.WorldDate.Display}** with **{CharacterSchema.BoundedName(characterName)}**. " +
            $"Scene ID: `{ShortId(scene.Id)}`. Ongoing scenes: **{result.Capacity.Active}/{result.Capacity.Limit}**.");
    }

    public static async Task HandleTrackerAsync(
        SocketSlashCommand command,
        UniverseStore universes,
        SceneStore scenes,
        CharacterStore characters,
        PermissionService permissions)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("Scene commands can only be used in a server.", ephemeral: true);
            return;
        }

        var root = command.Data.Options.First();
        if (root.Name is "settings" or "slots")
        {
            await HandleSettingsAsync(command, guildId, root, scenes);
            return;
        }
        if (root.Name == "create")
        {
            await HandleCreateAsync(command, universes, scenes, characters);
            return;
        }

        if (root.Name == "history")
        {
            await SendHistoryAsync(command, await scenes.GetAllAsync(guildId));
            return;
        }

        var action = root.Name == "edit" ? root.Options.First() : root;
        var sceneId = (string)Option(action.Options, "scene").Value;
        var scene = await scenes.GetAsync(guildId, sceneId);
        if (scene is null)
        {
            await command.RespondAsync("That scene does not exist.", ephemeral: true);
            return;
        }

        if (root.Name == "view")
        {
            await command.RespondAsync(
                TrimMessage(FormatSceneDetails(scene)),
                allowedMentions: AllowedMentions.None);
            return;
        }

        if (scene.IsCompleted)
        {
            await command.RespondAsync("That active scene no longer exists.", ephemeral: true);
            return;
        }

        if (!await CanManageAsync(guildId, command.User, scene, permissions))
        {
            await command.RespondAsync(
                "You need `scenetracker.manage.any`, or you must be a participant with `scenetracker.manage.own`.",
                ephemeral: true);
            return;
        }

        if (root.Name == "complete")
        {
            var completed = await scenes.CompleteAsync(guildId, sceneId);
            await command.RespondAsync(completed
                ? $"Scene **{scene.Title}** is now completed and has moved into history."
                : "That scene is no longer active.", ephemeral: true);
            return;
        }

        if (root.Name == "delete")
        {
            var deleted = await scenes.DeleteAsync(guildId, sceneId);
            await command.RespondAsync(deleted
                ? $"Scene **{scene.Title}** was permanently deleted."
                : "That scene is no longer active.", ephemeral: true);
            return;
        }

        var user = (IUser)Option(action.Options, "user").Value;
        if (root.Name == "invite")
        {
            var characterInput = (string)Option(action.Options, "character").Value;
            var invitedCharacter = await characters.GetAsync(guildId, user.Id, characterInput);
            if (invitedCharacter is null)
            {
                await command.RespondAsync($"That character is not stored under {user.Mention}.", ephemeral: true);
                return;
            }
            var characterName = invitedCharacter.Name;
            var characterDisplay = CharacterSchema.BoundedName(characterName);

            if (scene.Participants.Any(participant =>
                    participant.UserId == user.Id &&
                    participant.Characters.Contains(characterName, StringComparer.OrdinalIgnoreCase)))
            {
                await command.RespondAsync("That character is already in the scene.", ephemeral: true);
                return;
            }
            if (await scenes.HasPendingInviteAsync(guildId, sceneId, user.Id, characterName))
            {
                await command.RespondAsync("That character already has a pending invitation to this scene.", ephemeral: true);
                return;
            }

            await command.DeferAsync(ephemeral: true);
            var capacity = await scenes.GetCapacityAsync(guildId, user.Id, invitedCharacter.PublicId);
            if (capacity.IsFull)
            {
                await command.ModifyOriginalResponseAsync(p => p.Content = CapacityMessage(capacity));
                return;
            }
            var settings = await scenes.GetSettingsAsync(guildId);
            var replyName = SceneDialogue.ReplyName(settings, (command.Channel as SocketGuildChannel)!.Guild.CurrentUser.DisplayName);
            var invitationMessage = await command.Channel.SendMessageAsync(
                $"{user.Mention}, <@{command.User.Id}> invited your character **{characterDisplay}** to " +
                $"scene **{scene.Title}** (`{scene.WorldDate.Display}`).\n" +
                $"Reply to this message with `Accept, {replyName}.` or `Decline, {replyName}.`");
            var inviteStatus = await scenes.AddPendingInviteAsync(guildId, new PendingSceneInvite
            {
                InvitationMessageId = invitationMessage.Id,
                ChannelId = command.Channel.Id,
                SceneId = scene.Id,
                InvitedUserId = user.Id,
                CharacterName = characterName,
                CharacterId = invitedCharacter.PublicId,
                InvitedBy = command.User.Id
            });
            if (inviteStatus != SceneMutationStatus.Success)
            {
                await invitationMessage.ModifyAsync(properties =>
                    properties.Content = "This scene invitation could not be created because the scene or invitation state changed.");
                await command.ModifyOriginalResponseAsync(properties => properties.Content = inviteStatus switch
                {
                    SceneMutationStatus.AlreadyExists => "That character is already in the scene.",
                    SceneMutationStatus.InvitePending => "That character already has a pending invitation to this scene.",
                    SceneMutationStatus.AtCapacity => "That character has reached its ongoing scene limit.",
                    _ => "That scene is no longer active."
                });
                return;
            }

            await command.ModifyOriginalResponseAsync(properties =>
                properties.Content = $"Invitation sent to {user.Mention} for **{characterDisplay}** in **{scene.Title}**.");
            return;
        }

        if (action.Name == "remove-user")
        {
            var status = await scenes.RemoveUserAsync(guildId, sceneId, user.Id);
            await command.RespondAsync(status == SceneMutationStatus.Success
                ? $"Removed {user.Mention} and all of their characters from **{scene.Title}**."
                : "That member is not part of the scene.", ephemeral: true);
            return;
        }

        var removedCharacterInput = (string)Option(action.Options, "character").Value;
        var storedCharacter = await characters.GetAsync(guildId, user.Id, removedCharacterInput);
        if (storedCharacter is null)
        {
            await command.RespondAsync("That character is no longer stored under that member.", ephemeral: true);
            return;
        }
        var removedCharacter = storedCharacter.Name;
        var removedCharacterDisplay = CharacterSchema.BoundedName(removedCharacter);
        var removeStatus = await scenes.RemoveCharacterAsync(guildId, sceneId, user.Id, removedCharacter);
        await command.RespondAsync(removeStatus switch
        {
            SceneMutationStatus.Success => $"Removed {user.Mention}'s **{removedCharacterDisplay}** from **{scene.Title}**.",
            SceneMutationStatus.ParticipantNotFound => "That member is not part of the scene.",
            SceneMutationStatus.CharacterNotFound => "That character is not part of the scene.",
            _ => "That scene is no longer active."
        }, ephemeral: true);
    }

    public static async Task HandleAutocompleteAsync(
        SocketAutocompleteInteraction interaction,
        SceneStore scenes,
        CharacterStore characters)
    {
        if (interaction.GuildId is not { } guildId)
        {
            await interaction.RespondAsync([]);
            return;
        }

        var typed = interaction.Data.Current.Value?.ToString() ?? string.Empty;
        if (interaction.Data.Current.Name == "scene")
        {
            var active = await scenes.GetActiveAsync(guildId);
            await interaction.RespondAsync(active
                .Where(scene => SceneLabel(scene).Contains(typed, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .Select(scene => new AutocompleteResult(SceneLabel(scene), scene.Id)));
            return;
        }

        if (interaction.Data.Current.Name == "character")
        {
            var userId = ReadUserId(interaction.Data.Options) ?? interaction.User.Id;
            await CharacterAutocomplete.RespondAsync(interaction, characters, guildId, userId, typed);
            return;
        }

        await interaction.RespondAsync([]);
    }

    public static async Task<bool> HandleInviteReplyAsync(
        SocketMessage message,
        SceneStore scenes,
        CharacterStore characters)
    {
        if (message.Author.IsBot ||
            message.Channel is not SocketGuildChannel channel ||
            message is not SocketUserMessage userMessage)
            return false;

        ulong? referencedMessageId = null;
        if (userMessage.Reference?.MessageId.IsSpecified == true)
            referencedMessageId = userMessage.Reference.MessageId.Value;
        else if (userMessage.ReferencedMessage is not null)
            referencedMessageId = userMessage.ReferencedMessage.Id;
        if (referencedMessageId is null) return false;

        var pending = await scenes.GetPendingInviteAsync(channel.Guild.Id, referencedMessageId.Value);
        if (pending is null) return false;
        var pendingCharacterDisplay = CharacterSchema.BoundedName(pending.CharacterName);
        if (message.Author.Id != pending.InvitedUserId)
        {
            await message.Channel.SendMessageAsync($"Only <@{pending.InvitedUserId}> can answer that scene invitation.");
            return true;
        }

        var settings = await scenes.GetSettingsAsync(channel.Guild.Id);
        var replyName = SceneDialogue.ReplyName(settings, channel.Guild.CurrentUser.DisplayName);
        var accepted = SceneDialogue.IsReply(message.Content, "accept", replyName) ||
                       SceneDialogue.IsReply(message.Content, "confirm", replyName) || message.Content.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
        var declined = SceneDialogue.IsReply(message.Content, "decline", replyName) ||
                       SceneDialogue.IsReply(message.Content, "cancel", replyName) || message.Content.Trim().Equals("no", StringComparison.OrdinalIgnoreCase);
        if (!accepted && !declined)
        {
            await message.Channel.SendMessageAsync(
                $"{message.Author.Mention}, reply to the invitation with `Accept, {replyName}.` or `Decline, {replyName}.`");
            return true;
        }

        if (declined)
        {
            await scenes.RemovePendingInviteAsync(channel.Guild.Id, pending.InvitationMessageId);
            await UpdateInvitationAsync(
                channel.Guild,
                pending,
                $"{message.Author.Mention} declined the invitation for **{pendingCharacterDisplay}**.");
            return true;
        }

        var scene = await scenes.GetAsync(channel.Guild.Id, pending.SceneId);
        if (scene is null || scene.IsCompleted)
        {
            await scenes.RemovePendingInviteAsync(channel.Guild.Id, pending.InvitationMessageId);
            await UpdateInvitationAsync(channel.Guild, pending, "This invitation expired because the scene is no longer active.");
            return true;
        }

        var invitedCharacter = await characters.GetAsync(channel.Guild.Id, pending.InvitedUserId,
            pending.CharacterId == Guid.Empty ? pending.CharacterName : CharacterSchema.Selector(pending.CharacterId));
        if (invitedCharacter is null)
        {
            await scenes.RemovePendingInviteAsync(channel.Guild.Id, pending.InvitationMessageId);
            await UpdateInvitationAsync(
                channel.Guild,
                pending,
                $"This invitation expired because **{pendingCharacterDisplay}** is no longer stored for {message.Author.Mention}.");
            return true;
        }

        var status = await scenes.AddCharacterAsync(
            channel.Guild.Id,
            pending.SceneId,
            pending.InvitedUserId,
            invitedCharacter);
        if (status == SceneMutationStatus.AtCapacity)
        {
            await message.Channel.SendMessageAsync($"{message.Author.Mention}, " +
                CapacityMessage(await scenes.GetCapacityAsync(channel.Guild.Id, pending.InvitedUserId, invitedCharacter.PublicId)) +
                " Your invitation is still open; reply again after a slot becomes available.");
            return true;
        }
        await scenes.RemovePendingInviteAsync(channel.Guild.Id, pending.InvitationMessageId);
        await UpdateInvitationAsync(channel.Guild, pending, status switch
        {
            SceneMutationStatus.Success =>
                TrimMessage(SceneDialogue.Render(settings.AcceptanceMessage, message.Author.Mention,
                    CharacterSchema.BoundedName(invitedCharacter.Name), scene.Title, scene.WorldDate.Display)),
            SceneMutationStatus.AlreadyExists =>
                $"{message.Author.Mention}'s **{pendingCharacterDisplay}** is already part of scene **{scene.Title}**.",
            _ => "This invitation expired because the scene is no longer active."
        });
        return true;
    }

    private static async Task SendHistoryAsync(SocketSlashCommand command, IReadOnlyList<SceneRecord> scenes)
    {
        if (scenes.Count == 0)
        {
            await command.RespondAsync("No current or past scenes are stored.");
            return;
        }

        var pages = new List<string>();
        var page = "**Scene history**\n\n";
        foreach (var scene in scenes)
        {
            var participants = scene.Participants.Count == 0
                ? "None"
                : string.Join("; ", scene.Participants.Select(participant =>
                    $"<@{participant.UserId}>: {string.Join(", ", participant.Characters)}"));
            var status = scene.IsCompleted ? "COMPLETED" : "ACTIVE";
            var block = $"**[{status}] {scene.Title}** — `{scene.WorldDate.Display}` — `{ShortId(scene.Id)}`\n" +
                        $"Participants: {participants}\n\n";
            if (block.Length > 1750) block = block[..1747] + "...\n\n";
            if (page.Length + block.Length > 1900)
            {
                pages.Add(page);
                page = "**Scene history (continued)**\n\n";
            }
            page += block;
        }
        if (page.Length > 0) pages.Add(page);

        await command.RespondAsync(pages[0], allowedMentions: AllowedMentions.None);
        foreach (var continuation in pages.Skip(1))
            await command.FollowupAsync(continuation, allowedMentions: AllowedMentions.None);
    }

    private static string FormatSceneDetails(SceneRecord scene)
    {
        var status = scene.IsCompleted ? "Completed" : "Active";
        var participants = scene.Participants.Count == 0
            ? "- None"
            : string.Join("\n", scene.Participants.Select(participant =>
                $"- <@{participant.UserId}> — {string.Join(", ", participant.Characters.Select(character => $"**{character}**"))}"));
        return $"## {scene.Title}\n" +
               $"**Status:** {status}\n" +
               $"**World date:** {scene.WorldDate.Display}\n" +
               $"**Created by:** <@{scene.CreatedBy}>\n" +
               $"**Scene ID:** `{ShortId(scene.Id)}`\n\n" +
               $"**Participants and characters**\n{participants}";
    }

    private static async Task<bool> CanManageAsync(
        ulong guildId,
        IUser user,
        SceneRecord scene,
        PermissionService permissions) =>
        await permissions.HasAsync(guildId, user, "scenetracker.manage.any") ||
        (scene.Participants.Any(participant => participant.UserId == user.Id) &&
         await permissions.HasAsync(guildId, user, "scenetracker.manage.own"));

    private static async Task UpdateInvitationAsync(
        SocketGuild guild,
        PendingSceneInvite invite,
        string content)
    {
        try
        {
            if (guild.GetChannel(invite.ChannelId) is not IMessageChannel channel) return;
            if (await channel.GetMessageAsync(invite.InvitationMessageId) is IUserMessage invitation)
                await invitation.ModifyAsync(properties => properties.Content = content);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not update scene invitation {invite.InvitationMessageId}: {exception.Message}");
        }
    }

    private static string SceneLabel(SceneRecord scene)
    {
        var suffix = $" — {scene.WorldDate.Display} [{ShortId(scene.Id)}]";
        var maximumTitleLength = 100 - suffix.Length;
        var title = scene.Title.Length <= maximumTitleLength
            ? scene.Title
            : scene.Title[..Math.Max(1, maximumTitleLength - 3)] + "...";
        return title + suffix;
    }

    private static string ShortId(string sceneId) => sceneId[..Math.Min(8, sceneId.Length)];

    private static string CapacityMessage(SceneCapacity capacity) =>
        $"This character has **{capacity.Active}/{capacity.Limit}** ongoing scenes. Complete a scene first, or ask a moderator for extra slots.";

    private static SlashCommandOptionBuilder SlotAction(string name, bool amount)
    {
        var action = new SlashCommandOptionBuilder().WithName(name)
            .WithDescription($"{name} a member's extra scene slots per character.")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption("user", ApplicationCommandOptionType.User, "Member", isRequired: true);
        if (amount) action.AddOption(new SlashCommandOptionBuilder().WithName("amount")
            .WithDescription("Extra slots per character").WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(true).WithMinValue(1).WithMaxValue(1000));
        return action;
    }

    private static async Task HandleSettingsAsync(SocketSlashCommand command, ulong guildId,
        SocketSlashCommandDataOption root, SceneStore scenes)
    {
        await command.DeferAsync(ephemeral: true);
        if (root.Name == "settings")
        {
            var limitOption = root.Options.FirstOrDefault(o => o.Name == "limit");
            var replyName = OptionalString(root.Options, "reply-name");
            var template = OptionalString(root.Options, "acceptance-message");
            if ((template is not null && !SceneDialogue.IsValidTemplate(template)) ||
                (replyName is not null && (string.IsNullOrWhiteSpace(replyName) || replyName.Any(char.IsControl) || replyName.Contains('`'))))
            {
                await command.ModifyOriginalResponseAsync(p => p.Content =
                    "Use a plain reply name and a nonempty message of at most 1,500 characters. The expanded message must fit within 2,000 characters.");
                return;
            }
            if (root.Options.Count > 0)
                await scenes.ConfigureAsync(guildId, limitOption is null ? null : Convert.ToInt32(limitOption.Value), replyName, template);
            var settings = await scenes.GetSettingsAsync(guildId);
            var name = SceneDialogue.ReplyName(settings, ((SocketGuildChannel)command.Channel).Guild.CurrentUser.DisplayName);
            await command.ModifyOriginalResponseAsync(p => p.Content =
                $"**Scene settings**\nOngoing limit per character: **{settings.ActiveLimitPerCharacter}**\n" +
                $"Invitation reply: `Accept, {name}.` (plain `Accept` also works)\n" +
                $"Acceptance message:\n{settings.AcceptanceMessage}\n\nExisting scenes are retained when limits are lowered.");
            return;
        }
        var action = root.Options.First();
        var user = (IUser)Option(action.Options, "user").Value;
        if (action.Name != "view")
        {
            var amount = action.Name == "reset" ? 0 : Convert.ToInt32(Option(action.Options, "amount").Value);
            await scenes.ChangeSlotsAsync(guildId, user.Id, action.Name == "remove" ? -amount : amount, action.Name == "reset");
        }
        var current = await scenes.GetSettingsAsync(guildId);
        var extra = current.ExtraSlotsByUser.GetValueOrDefault(user.Id);
        await command.ModifyOriginalResponseAsync(p => p.Content =
            $"{user.Mention} has **{extra}** extra slots: **{current.ActiveLimitPerCharacter + extra}** ongoing scenes per character " +
            $"({current.ActiveLimitPerCharacter} default + {extra} extra). Existing scenes are retained.");
    }

    private static string TrimMessage(string content) =>
        content.Length <= 2000 ? content : content[..1997] + "...";

    private static SlashCommandOptionBuilder SceneAction(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(SceneOption());

    private static SlashCommandOptionBuilder SceneOption() =>
        new SlashCommandOptionBuilder()
            .WithName("scene")
            .WithDescription("Active scene")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .WithAutocomplete(true);

    private static SlashCommandOptionBuilder CharacterOption(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription($"{description} ({CharacterSchema.NameMaxLength} characters maximum)")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .WithMaxLength(CharacterSchema.NameMaxLength)
            .WithAutocomplete(true);

    private static SocketSlashCommandDataOption Option(
        IReadOnlyCollection<SocketSlashCommandDataOption> options,
        string name) => options.First(option => option.Name == name);

    private static string? OptionalString(
        IReadOnlyCollection<SocketSlashCommandDataOption> options,
        string name) => options.FirstOrDefault(option => option.Name == name)?.Value as string;

    private static ulong? ReadUserId(IReadOnlyCollection<AutocompleteOption> options)
    {
        var value = options.FirstOrDefault(option => option.Name == "user")?.Value;
        if (value is IUser user) return user.Id;
        return ulong.TryParse(value?.ToString(), out var id) ? id : null;
    }
}
