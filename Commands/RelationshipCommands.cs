using System.Text;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class RelationshipCommands
{
    public static ApplicationCommandProperties Build()
    {
        var request = new SlashCommandOptionBuilder()
            .WithName("request")
            .WithDescription("Requests a relationship between your character and another character.")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(CharacterOption("my-character", "Your character"))
            .AddOption("user", ApplicationCommandOptionType.User, "Owner of the other character", isRequired: true)
            .AddOption(CharacterOption("their-character", "Their character"))
            .AddOption(AutocompleteOption("relation", "Relationship from your character's perspective"));

        var requests = new SlashCommandOptionBuilder()
            .WithName("requests")
            .WithDescription("Lists your incoming and outgoing relationship requests.")
            .WithType(ApplicationCommandOptionType.SubCommand);

        var approve = RequestAction("approve", "Approves an incoming relationship request.");
        var decline = RequestAction("decline", "Declines an incoming relationship request.");

        var remove = new SlashCommandOptionBuilder()
            .WithName("remove")
            .WithDescription("Removes one of your character's direct relationships.")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(CharacterOption("character", "Your character"))
            .AddOption(AutocompleteOption("relationship", "Direct relationship to remove"));

        var view = new SlashCommandOptionBuilder()
            .WithName("view")
            .WithDescription("Shows direct and inferred relationships for a character.")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption("user", ApplicationCommandOptionType.User, "Character owner", isRequired: true)
            .AddOption(CharacterOption("character", "Character to inspect"));

        return new SlashCommandBuilder()
            .WithName("relationship")
            .WithDescription("Manages relationships between roleplay characters.")
            .AddOption(request)
            .AddOption(requests)
            .AddOption(approve)
            .AddOption(decline)
            .AddOption(remove)
            .AddOption(view)
            .Build();
    }

    public static async Task HandleAsync(
        SocketSlashCommand command,
        CharacterStore characters,
        RelationshipStore relationships,
        RelationshipInferenceEngine inference)
    {
        if (command.GuildId is not { } guildId || command.Channel is not SocketGuildChannel guildChannel)
        {
            await command.RespondAsync("Relationship commands can only be used in a server.", ephemeral: true);
            return;
        }

        var subcommand = command.Data.Options.First();
        switch (subcommand.Name)
        {
            case "request":
                await RequestAsync(command, guildId, subcommand, characters, relationships);
                return;
            case "requests":
                await ListRequestsAsync(command, guildId, characters, relationships);
                return;
            case "approve":
            case "decline":
                await RespondToRequestAsync(
                    command,
                    guildChannel.Guild,
                    guildId,
                    (string)Option(subcommand.Options, "request").Value,
                    subcommand.Name == "approve",
                    characters,
                    relationships);
                return;
            case "remove":
                await RemoveAsync(command, guildId, subcommand, characters, relationships);
                return;
            case "view":
                await ViewAsync(command, guildId, subcommand, characters, relationships, inference);
                return;
        }
    }

    public static async Task HandleAutocompleteAsync(
        SocketAutocompleteInteraction interaction,
        CharacterStore characters,
        RelationshipStore relationships,
        RelationshipInferenceEngine inference)
    {
        if (interaction.GuildId is not { } guildId)
        {
            await interaction.RespondAsync([]);
            return;
        }

        var options = interaction.Data.Options;
        var current = interaction.Data.Current.Name;
        var typed = interaction.Data.Current.Value?.ToString() ?? string.Empty;

        if (current == "my-character" ||
            current == "character" && !options.Any(option => option.Name == "user"))
        {
            await RespondNamesAsync(interaction, await characters.GetCharacterNamesAsync(guildId, interaction.User.Id), typed);
            return;
        }

        if (current == "their-character" || current == "character")
        {
            var userId = ReadUserId(options);
            var names = userId is null ? [] : await characters.GetCharacterNamesAsync(guildId, userId.Value);
            await RespondNamesAsync(interaction, names, typed);
            return;
        }

        if (current == "relation")
        {
            await RespondRelationsAsync(interaction, guildId, options, typed, characters, relationships, inference);
            return;
        }

        if (current == "request")
        {
            var pending = await relationships.GetRequestsForUserAsync(guildId, interaction.User.Id);
            var owned = (await characters.GetAllOwnedAsync(guildId))
                .ToDictionary(item => item.Character.PublicId);
            await interaction.RespondAsync(pending
                .Where(request => request.TargetOwnerId == interaction.User.Id)
                .Select(request => new
                {
                    Request = request,
                    Label = RequestLabel(request, owned, incoming: true)
                })
                .Where(item => item.Label.Contains(typed, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .Select(item => new AutocompleteResult(TrimChoice(item.Label), item.Request.Id)));
            return;
        }

        if (current == "relationship")
        {
            var characterName = options.FirstOrDefault(option => option.Name == "character")?.Value?.ToString();
            var character = string.IsNullOrWhiteSpace(characterName)
                ? null
                : await characters.GetAsync(guildId, interaction.User.Id, characterName);
            if (character is null)
            {
                await interaction.RespondAsync([]);
                return;
            }

            var owned = (await characters.GetAllOwnedAsync(guildId)).ToDictionary(item => item.Character.PublicId);
            var direct = await relationships.GetDirectAsync(guildId);
            await interaction.RespondAsync(direct
                .Where(item => item.SourceCharacterId == character.PublicId || item.TargetCharacterId == character.PublicId)
                .Select(item => new
                {
                    Relationship = item,
                    Label = DirectRelationshipLabel(item, character.PublicId, owned)
                })
                .Where(item => item.Label.Contains(typed, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .Select(item => new AutocompleteResult(TrimChoice(item.Label), item.Relationship.Id)));
            return;
        }

        await interaction.RespondAsync([]);
    }

    public static async Task<bool> HandleReplyAsync(
        SocketMessage message,
        CharacterStore characters,
        RelationshipStore relationships,
        PermissionService permissions)
    {
        if (message.Author.IsBot ||
            message.Channel is not SocketGuildChannel channel ||
            message is not SocketUserMessage userMessage)
            return false;

        var referencedMessageId = ReferencedMessageId(userMessage);
        if (referencedMessageId is null) return false;
        var pending = await relationships.GetPendingByMessageAsync(channel.Guild.Id, referencedMessageId.Value);
        if (pending is null) return false;

        if (pending.TargetOwnerId != message.Author.Id)
        {
            await message.Channel.SendMessageAsync($"Only <@{pending.TargetOwnerId}> can answer that relationship request.");
            return true;
        }
        if (!await permissions.HasAsync(channel.Guild.Id, message.Author, "relationship.respond"))
        {
            await message.Channel.SendMessageAsync(
                $"{message.Author.Mention}, you no longer have permission to answer relationship requests.");
            return true;
        }

        var reply = NormalizeReply(message.Content);
        var accepted = reply is "accept" or "accept yoko" or "approve" or "yes";
        var declined = reply is "decline" or "decline yoko" or "reject" or "no" or "cancel";
        if (!accepted && !declined)
        {
            await message.Channel.SendMessageAsync(
                $"{message.Author.Mention}, reply directly with `Accept` or `Decline`.");
            return true;
        }

        if (declined)
        {
            var declinedResult = await relationships.DeclineAsync(channel.Guild.Id, pending.Id, message.Author.Id);
            if (declinedResult.Status == RelationshipMutationStatus.Success)
                await UpdateInvitationAsync(channel.Guild, pending, $"{message.Author.Mention} declined this relationship request.");
            return true;
        }

        var source = await characters.GetByPublicIdAsync(channel.Guild.Id, pending.SourceCharacterId);
        var target = await characters.GetByPublicIdAsync(channel.Guild.Id, pending.TargetCharacterId);
        if (source is null || target is null)
        {
            await relationships.DeclineAsync(channel.Guild.Id, pending.Id, message.Author.Id);
            await UpdateInvitationAsync(channel.Guild, pending, "This request expired because one of its characters no longer exists.");
            return true;
        }

        var result = await relationships.ApproveAsync(channel.Guild.Id, pending.Id, message.Author.Id);
        await UpdateInvitationAsync(channel.Guild, pending, ApprovalMessage(result, source.Character.Name, target.Character.Name));
        return true;
    }

    private static async Task RequestAsync(
        SocketSlashCommand command,
        ulong guildId,
        SocketSlashCommandDataOption subcommand,
        CharacterStore characters,
        RelationshipStore relationships)
    {
        var sourceName = (string)Option(subcommand.Options, "my-character").Value;
        var targetUser = (IUser)Option(subcommand.Options, "user").Value;
        var targetName = (string)Option(subcommand.Options, "their-character").Value;
        var requestedType = (string)Option(subcommand.Options, "relation").Value;
        var definition = RelationshipCatalog.Resolve(requestedType);
        if (definition is not { Requestable: true })
        {
            await command.RespondAsync("Choose one of the requestable biological relationships from autocomplete.", ephemeral: true);
            return;
        }
        if (targetUser.IsBot)
        {
            await command.RespondAsync("Bot accounts cannot own character relationships.", ephemeral: true);
            return;
        }

        var source = await characters.GetAsync(guildId, command.User.Id, sourceName);
        var target = await characters.GetAsync(guildId, targetUser.Id, targetName);
        if (source is null)
        {
            await command.RespondAsync($"**{sourceName}** is not stored under your account.", ephemeral: true);
            return;
        }
        if (target is null)
        {
            await command.RespondAsync($"**{targetName}** is not stored under {targetUser.Mention}.", ephemeral: true);
            return;
        }
        if (source.PublicId == target.PublicId)
        {
            await command.RespondAsync("A character cannot have a biological relationship with itself.", ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);
        var inverse = RelationshipCatalog.Get(definition.InverseId)!;
        var invitation = await command.Channel.SendMessageAsync(
            $"{targetUser.Mention}, **{source.Name}** is requesting a biological relationship with **{target.Name}**.\n" +
            $"- **{source.Name}** → **{definition.DisplayName}** of **{target.Name}**\n" +
            $"- **{target.Name}** → **{inverse.DisplayName}** of **{source.Name}**\n\n" +
            "Reply directly to this message with `Accept` or `Decline`.");

        var pending = new PendingRelationshipRequest
        {
            InvitationMessageId = invitation.Id,
            ChannelId = command.Channel.Id,
            SourceCharacterId = source.PublicId,
            SourceOwnerId = command.User.Id,
            TargetCharacterId = target.PublicId,
            TargetOwnerId = targetUser.Id,
            TypeId = definition.Id
        };
        var status = await relationships.AddPendingAsync(guildId, pending);
        if (status != RelationshipMutationStatus.Success)
        {
            await invitation.ModifyAsync(properties => properties.Content = status == RelationshipMutationStatus.AlreadyExists
                ? "This relationship already exists."
                : "An equivalent relationship request is already pending.");
            await command.ModifyOriginalResponseAsync(properties => properties.Content = status == RelationshipMutationStatus.AlreadyExists
                ? "That relationship already exists."
                : "An equivalent relationship request is already pending.");
            return;
        }

        await command.ModifyOriginalResponseAsync(properties =>
            properties.Content = $"Relationship request `{ShortId(pending.Id)}` sent to {targetUser.Mention}.");
    }

    private static async Task ListRequestsAsync(
        SocketSlashCommand command,
        ulong guildId,
        CharacterStore characters,
        RelationshipStore relationships)
    {
        var pending = await relationships.GetRequestsForUserAsync(guildId, command.User.Id);
        if (pending.Count == 0)
        {
            await command.RespondAsync("You have no pending relationship requests.", ephemeral: true);
            return;
        }

        var owned = (await characters.GetAllOwnedAsync(guildId)).ToDictionary(item => item.Character.PublicId);
        var lines = pending.Select(request =>
            $"- `{ShortId(request.Id)}` {(request.TargetOwnerId == command.User.Id ? "INCOMING" : "OUTGOING")} — " +
            RequestLabel(request, owned, request.TargetOwnerId == command.User.Id));
        var content = "**Pending relationship requests**\n" + string.Join("\n", lines);
        await command.RespondAsync(TrimMessage(content), ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    private static async Task RespondToRequestAsync(
        SocketSlashCommand command,
        SocketGuild guild,
        ulong guildId,
        string requestId,
        bool approve,
        CharacterStore characters,
        RelationshipStore relationships)
    {
        var pending = await relationships.GetPendingAsync(guildId, requestId);
        if (pending is null)
        {
            await command.RespondAsync("That pending request was not found or its shortened ID is ambiguous.", ephemeral: true);
            return;
        }
        if (pending.TargetOwnerId != command.User.Id)
        {
            await command.RespondAsync("Only the owner of the receiving character can answer that request.", ephemeral: true);
            return;
        }

        var source = await characters.GetByPublicIdAsync(guildId, pending.SourceCharacterId);
        var target = await characters.GetByPublicIdAsync(guildId, pending.TargetCharacterId);
        if (source is null || target is null)
        {
            await relationships.DeclineAsync(guildId, pending.Id, command.User.Id);
            await UpdateInvitationAsync(guild, pending, "This request expired because one of its characters no longer exists.");
            await command.RespondAsync("The request expired because one of its characters no longer exists.", ephemeral: true);
            return;
        }

        var result = approve
            ? await relationships.ApproveAsync(guildId, pending.Id, command.User.Id)
            : await relationships.DeclineAsync(guildId, pending.Id, command.User.Id);
        var message = approve
            ? ApprovalMessage(result, source.Character.Name, target.Character.Name)
            : $"{command.User.Mention} declined this relationship request.";
        if (result.Status == RelationshipMutationStatus.Success ||
            result.Status == RelationshipMutationStatus.AlreadyExists)
            await UpdateInvitationAsync(guild, pending, message);

        await command.RespondAsync(result.Status switch
        {
            RelationshipMutationStatus.Success when approve =>
                $"Approved. **{source.Character.Name}** and **{target.Character.Name}** now show the accompanying relationship perspectives.",
            RelationshipMutationStatus.Success => "Relationship request declined.",
            RelationshipMutationStatus.AlreadyExists => "That relationship already exists; the duplicate request was cleared.",
            RelationshipMutationStatus.NotAuthorized => "Only the receiving character's owner can answer that request.",
            RelationshipMutationStatus.Ambiguous => "That shortened request ID matches more than one request.",
            _ => "That pending request no longer exists."
        }, ephemeral: true);
    }

    private static async Task RemoveAsync(
        SocketSlashCommand command,
        ulong guildId,
        SocketSlashCommandDataOption subcommand,
        CharacterStore characters,
        RelationshipStore relationships)
    {
        var characterName = (string)Option(subcommand.Options, "character").Value;
        var relationshipId = (string)Option(subcommand.Options, "relationship").Value;
        var character = await characters.GetAsync(guildId, command.User.Id, characterName);
        if (character is null)
        {
            await command.RespondAsync("That character is not stored under your account.", ephemeral: true);
            return;
        }

        var result = await relationships.RemoveAsync(
            guildId, relationshipId, character.PublicId, command.User.Id);
        await command.RespondAsync(result.Status switch
        {
            RelationshipMutationStatus.Success =>
                "The direct relationship was removed. Every relationship inferred from it has been recalculated and any unsupported results disappeared.",
            RelationshipMutationStatus.NotAuthorized => "You do not own either character in that relationship.",
            RelationshipMutationStatus.Ambiguous => "That shortened relationship ID matches more than one relationship.",
            _ => "That direct relationship was not found for the selected character."
        }, ephemeral: true);
    }

    private static async Task ViewAsync(
        SocketSlashCommand command,
        ulong guildId,
        SocketSlashCommandDataOption subcommand,
        CharacterStore characters,
        RelationshipStore relationships,
        RelationshipInferenceEngine inference)
    {
        var user = (IUser)Option(subcommand.Options, "user").Value;
        var characterName = (string)Option(subcommand.Options, "character").Value;
        var character = await characters.GetAsync(guildId, user.Id, characterName);
        if (character is null)
        {
            await command.RespondAsync($"**{characterName}** is not stored under {user.Mention}.", ephemeral: true);
            return;
        }

        var owned = (await characters.GetAllOwnedAsync(guildId)).ToDictionary(item => item.Character.PublicId);
        var edges = inference.Build(await relationships.GetDirectAsync(guildId))
            .Where(edge => edge.SourceCharacterId == character.PublicId)
            .Where(edge => owned.ContainsKey(edge.TargetCharacterId))
            .OrderBy(edge => edge.IsInferred)
            .ThenBy(edge => RelationshipCatalog.Get(edge.TypeId)?.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => owned[edge.TargetCharacterId].Character.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (edges.Length == 0)
        {
            await command.RespondAsync($"**{character.Name}** has no direct or inferred biological relationships.");
            return;
        }

        var direct = edges.Where(edge => !edge.IsInferred).ToArray();
        var inferred = edges.Where(edge => edge.IsInferred).ToArray();
        var builder = new StringBuilder($"## Biological relationships: {character.Name}\n");
        AppendEdges(builder, "Direct and approved", direct, owned);
        AppendEdges(builder, "Inferred in the background", inferred, owned);
        await command.RespondAsync(TrimMessage(builder.ToString()), allowedMentions: AllowedMentions.None);
    }

    private static async Task RespondRelationsAsync(
        SocketAutocompleteInteraction interaction,
        ulong guildId,
        IReadOnlyCollection<AutocompleteOption> options,
        string typed,
        CharacterStore characters,
        RelationshipStore relationships,
        RelationshipInferenceEngine inference)
    {
        var sourceName = options.FirstOrDefault(option => option.Name == "my-character")?.Value?.ToString();
        var targetName = options.FirstOrDefault(option => option.Name == "their-character")?.Value?.ToString();
        var targetUserId = ReadUserId(options);
        Character? source = null;
        Character? target = null;
        if (!string.IsNullOrWhiteSpace(sourceName))
            source = await characters.GetAsync(guildId, interaction.User.Id, sourceName);
        if (targetUserId is { } ownerId && !string.IsNullOrWhiteSpace(targetName))
            target = await characters.GetAsync(guildId, ownerId, targetName);

        var suggested = source is null || target is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : inference.Build(await relationships.GetDirectAsync(guildId))
                .Where(edge => edge.IsInferred &&
                               edge.SourceCharacterId == source.PublicId &&
                               edge.TargetCharacterId == target.PublicId)
                .Select(edge => edge.TypeId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var choices = RelationshipCatalog.Definitions
            .Where(definition => definition.Requestable && RelationshipCatalog.MatchesSearch(definition, typed))
            .OrderByDescending(definition => suggested.Contains(definition.Id))
            .ThenBy(definition => definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .Select(definition => new AutocompleteResult(
                TrimChoice(definition.DisplayName + (suggested.Contains(definition.Id) ? " — inferred from family graph" : string.Empty)),
                definition.Id));
        await interaction.RespondAsync(choices);
    }

    private static void AppendEdges(
        StringBuilder builder,
        string heading,
        IReadOnlyCollection<RelationshipEdge> edges,
        IReadOnlyDictionary<Guid, OwnedCharacter> characters)
    {
        if (edges.Count == 0) return;
        builder.AppendLine($"\n**{heading}**");
        foreach (var edge in edges.Take(30))
        {
            var target = characters[edge.TargetCharacterId];
            var definition = RelationshipCatalog.Get(edge.TypeId);
            builder.Append($"- **{definition?.DisplayName ?? edge.TypeId}** → **{target.Character.Name}** (<@{target.OwnerId}>)");
            if (edge.IsInferred && !string.IsNullOrWhiteSpace(edge.Explanation))
                builder.Append($" — *{edge.Explanation}*");
            else if (!edge.IsInferred && edge.RelationshipId is not null)
                builder.Append($" — `{ShortId(edge.RelationshipId)}`");
            builder.AppendLine();
        }
        if (edges.Count > 30) builder.AppendLine($"- …and {edges.Count - 30} more.");
    }

    private static string ApprovalMessage(
        RelationshipActionResult result,
        string sourceName,
        string targetName)
    {
        if (result.Status == RelationshipMutationStatus.AlreadyExists)
            return $"The relationship between **{sourceName}** and **{targetName}** already exists.";
        var definition = result.Relationship is null ? null : RelationshipCatalog.Get(result.Relationship.TypeId);
        var inverse = definition is null ? null : RelationshipCatalog.Get(definition.InverseId);
        return result.Status == RelationshipMutationStatus.Success
            ? $"Accepted. **{sourceName}** is **{definition?.DisplayName}** of **{targetName}**; " +
              $"**{targetName}** is **{inverse?.DisplayName}** of **{sourceName}**."
            : "This relationship request could not be approved.";
    }

    private static async Task UpdateInvitationAsync(
        SocketGuild guild,
        PendingRelationshipRequest request,
        string content)
    {
        try
        {
            if (guild.GetChannel(request.ChannelId) is not IMessageChannel channel) return;
            if (await channel.GetMessageAsync(request.InvitationMessageId) is IUserMessage invitation)
                await invitation.ModifyAsync(properties => properties.Content = content);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not update relationship request {request.Id}: {exception.Message}");
        }
    }

    private static string RequestLabel(
        PendingRelationshipRequest request,
        IReadOnlyDictionary<Guid, OwnedCharacter> characters,
        bool incoming)
    {
        var source = characters.TryGetValue(request.SourceCharacterId, out var sourceCharacter)
            ? sourceCharacter.Character.Name
            : "deleted character";
        var target = characters.TryGetValue(request.TargetCharacterId, out var targetCharacter)
            ? targetCharacter.Character.Name
            : "deleted character";
        var definition = RelationshipCatalog.Get(request.TypeId);
        return incoming
            ? $"{source} requests {definition?.DisplayName ?? request.TypeId} of {target}"
            : $"{source} → {definition?.DisplayName ?? request.TypeId} → {target}";
    }

    private static string DirectRelationshipLabel(
        RelationshipRecord relationship,
        Guid characterId,
        IReadOnlyDictionary<Guid, OwnedCharacter> characters)
    {
        var fromSource = relationship.SourceCharacterId == characterId;
        var targetId = fromSource ? relationship.TargetCharacterId : relationship.SourceCharacterId;
        var typeId = fromSource
            ? relationship.TypeId
            : RelationshipCatalog.Get(relationship.TypeId)?.InverseId ?? relationship.TypeId;
        var targetName = characters.TryGetValue(targetId, out var target)
            ? target.Character.Name
            : "deleted character";
        return $"{RelationshipCatalog.Get(typeId)?.DisplayName ?? typeId} → {targetName} [{ShortId(relationship.Id)}]";
    }

    private static Task RespondNamesAsync(
        SocketAutocompleteInteraction interaction,
        IReadOnlyList<string> names,
        string typed) => interaction.RespondAsync(names
        .Where(name => name.Contains(typed, StringComparison.OrdinalIgnoreCase))
        .Take(25)
        .Select(name => new AutocompleteResult(name, name)));

    private static ulong? ReferencedMessageId(SocketUserMessage message)
    {
        if (message.Reference?.MessageId.IsSpecified == true) return message.Reference.MessageId.Value;
        return message.ReferencedMessage?.Id;
    }

    private static string NormalizeReply(string content) =>
        Regex.Replace(content.ToLowerInvariant(), @"[\s,.!]+", " ").Trim();

    private static SlashCommandOptionBuilder RequestAction(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(AutocompleteOption("request", "Incoming relationship request"));

    private static SlashCommandOptionBuilder CharacterOption(string name, string description) =>
        AutocompleteOption(name, description);

    private static SlashCommandOptionBuilder AutocompleteOption(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .WithAutocomplete(true);

    private static SocketSlashCommandDataOption Option(
        IReadOnlyCollection<SocketSlashCommandDataOption> options,
        string name) => options.First(option => option.Name == name);

    private static ulong? ReadUserId(IEnumerable<AutocompleteOption> options)
    {
        var value = options.FirstOrDefault(option => option.Name == "user")?.Value;
        if (value is IUser user) return user.Id;
        return ulong.TryParse(value?.ToString(), out var id) ? id : null;
    }

    private static string ShortId(string id) => id[..Math.Min(8, id.Length)];
    private static string TrimChoice(string value) => value.Length <= 100 ? value : value[..97] + "...";
    private static string TrimMessage(string value) => value.Length <= 1950 ? value : value[..1947] + "...";
}
