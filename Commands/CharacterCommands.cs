using System.Text;
using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class CharacterCommands
{
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong AdminId), FilloutSession> FilloutSessions = new();
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong AdminId), DeleteSession> DeleteSessions = new();
    public static ApplicationCommandProperties[] Build()
    {
        var approve = new SlashCommandOptionBuilder()
                .WithName("approve")
                .WithDescription("Approves and creates a user's character.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("user", ApplicationCommandOptionType.User, "Character owner", isRequired: true)
                .AddOption("character-name", ApplicationCommandOptionType.String, "Character name", isRequired: true)
                .AddOption(AutocompleteOption("age", "Optional age", required: false))
                .AddOption(AutocompleteOption("gender", "Optional gender", required: false))
                .AddOption(AutocompleteOption("region", "Optional region", required: false));

        var edit = new SlashCommandOptionBuilder()
                .WithName("edit")
                .WithDescription("Changes a baseline or custom character property.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("user", ApplicationCommandOptionType.User, "Character owner", isRequired: true)
                .AddOption(AutocompleteOption("character-name", "Character name"))
                .AddOption(AutocompleteOption("field", "Property to edit"))
                .AddOption("value", ApplicationCommandOptionType.String, "New value", isRequired: true);

        var removeField = new SlashCommandOptionBuilder()
                .WithName("remove-field")
                .WithDescription("Clears a baseline property or removes a custom property.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("user", ApplicationCommandOptionType.User, "Character owner", isRequired: true)
                .AddOption(AutocompleteOption("character-name", "Character name"))
                .AddOption(AutocompleteOption("field", "Property to remove"));

        var view = new SlashCommandOptionBuilder()
                .WithName("view")
                .WithDescription("Displays a stored character.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("user", ApplicationCommandOptionType.User, "Character owner", isRequired: true)
                .AddOption(AutocompleteOption("character-name", "Character name"));

        var delete = new SlashCommandOptionBuilder()
                .WithName("delete")
                .WithDescription("Permanently deletes a character after typed confirmation.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("user", ApplicationCommandOptionType.User, "Character owner", isRequired: true)
                .AddOption(AutocompleteOption("character-name", "Character name"));

        return
        [
            new SlashCommandBuilder()
                .WithName("character")
                .WithDescription("Manages roleplay characters.")
                .AddOption(approve)
                .AddOption(edit)
                .AddOption(view)
                .AddOption(removeField)
                .AddOption(delete)
                .Build()
        ];
    }

    public static async Task HandleAutocompleteAsync(
        SocketAutocompleteInteraction interaction,
        CharacterStore store,
        CharacterSettingsStore settings)
    {
        if (interaction.GuildId is not { } guildId)
        {
            await interaction.RespondAsync([]);
            return;
        }

        // Discord.Net exposes the selected leaf-subcommand options as a flat collection here.
        var options = interaction.Data.Options;
        var userId = ReadUserId(options);
        var currentName = interaction.Data.Current.Name;
        if (currentName is "age" or "gender" or "region")
        {
            await RespondWithMatchesAsync(interaction, await settings.GetAutofillAsync(guildId, currentName));
            return;
        }

        if (userId is null)
        {
            await interaction.RespondAsync([]);
            return;
        }

        IReadOnlyList<string> candidates;
        if (interaction.Data.Current.Name == "character-name")
        {
            candidates = await store.GetCharacterNamesAsync(guildId, userId.Value);
        }
        else if (interaction.Data.Current.Name == "field")
        {
            var characterName = options.FirstOrDefault(option => option.Name == "character-name")?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(characterName))
            {
                candidates = [];
            }
            else
            {
                var storedFields = await store.GetFieldNamesAsync(guildId, userId.Value, characterName);
                var defaultFields = await settings.GetDefaultPropertiesAsync(guildId);
                candidates = defaultFields.Concat(storedFields).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        else
        {
            candidates = [];
        }

        await RespondWithMatchesAsync(interaction, candidates);
    }

    private static async Task RespondWithMatchesAsync(SocketAutocompleteInteraction interaction, IReadOnlyList<string> candidates)
    {
        var typed = interaction.Data.Current.Value?.ToString() ?? string.Empty;
        var results = candidates
            .Where(candidate => candidate.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(candidate => new AutocompleteResult(candidate, candidate));
        await interaction.RespondAsync(results);
    }

    public static async Task HandleAsync(
        SocketSlashCommand command,
        CharacterStore store,
        CharacterSettingsStore settings,
        CharacterRoleService characterRoles)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("Character commands can only be used in a server.", ephemeral: true);
            return;
        }

        var subcommand = command.Data.Options.First();
        var user = (IUser)Option(subcommand.Options, "user").Value;
        var characterName = (string)Option(subcommand.Options, "character-name").Value;

        switch (subcommand.Name)
        {
            case "approve":
                await command.DeferAsync(ephemeral: true);
                var capacity = await characterRoles.GetCapacityAsync(guildId, user.Id);
                if (capacity.IsFull)
                {
                    await UpdateOriginalAsync(command,
                        $"{user.Mention} already has **{capacity.CharacterCount}** character(s), which reaches the configured " +
                        $"OC-role capacity of **{capacity.RoleCapacity}**. Add another role with `/charadmin roles add` before approving another character.");
                    break;
                }

                var created = await store.AddAsync(guildId, user.Id, characterName, command.User.Id);
                if (created is null)
                {
                    await UpdateOriginalAsync(command, $"{user.Mention} already has a character named **{characterName}**.");
                    break;
                }

                var roleSync = await characterRoles.SyncMemberAsync(guildId, user.Id);
                var roleNotice = RoleNotice(roleSync);
                var approvalDelivery = await SendApprovalMessagesAsync(command, settings, user, created.Name);
                var approvalMessageNotice = ApprovalMessageNotice(approvalDelivery);

                var suppliedFields = new Dictionary<string, string>();
                foreach (var prefillField in new[] { "age", "gender", "region" })
                {
                    if (OptionalString(subcommand.Options, prefillField) is { } suppliedValue)
                    {
                        await store.SetFieldAsync(guildId, user.Id, created.Name, prefillField, suppliedValue);
                        suppliedFields[prefillField] = suppliedValue;
                    }
                }

                var remainingFields = new List<FilloutField>();
                foreach (var property in await settings.GetDefaultPropertiesAsync(guildId))
                {
                    if (suppliedFields.ContainsKey(property)) continue;
                    var suggestions = await settings.GetAutofillAsync(guildId, property);
                    remainingFields.Add(new FilloutField(property, CharacterSchema.Label(property), suggestions));
                }

                if (remainingFields.Count == 0)
                {
                    await UpdateOriginalAsync(command,
                        $"Approved **{created.Name}** for {user.Mention}.{roleNotice}{approvalMessageNotice} No additional default fields are configured.");
                    break;
                }

                var session = new FilloutSession(guildId, user.Id, created.Name, remainingFields.ToArray(), command);
                DeleteSessions.TryRemove((command.Channel.Id, command.User.Id), out _);
                FilloutSessions[(command.Channel.Id, command.User.Id)] = session;
                await UpdateOriginalAsync(command,
                    $"Approved **{created.Name}** for {user.Mention}.{roleNotice}{approvalMessageNotice}\n\n{PromptFor(session)}");
                break;
            case "edit":
                var field = (string)Option(subcommand.Options, "field").Value;
                var value = (string)Option(subcommand.Options, "value").Value;
                var edited = await store.SetFieldAsync(guildId, user.Id, characterName, field, value);
                await command.RespondAsync(edited
                    ? $"Set **{field}** on **{characterName}** to `{value}`."
                    : "Character not found.", ephemeral: true);
                break;
            case "remove-field":
                var removedField = (string)Option(subcommand.Options, "field").Value;
                var removed = await store.RemoveFieldAsync(guildId, user.Id, characterName, removedField);
                await command.RespondAsync(removed
                    ? $"Removed **{removedField}** from **{characterName}**."
                    : "Character or property not found.", ephemeral: true);
                break;
            case "view":
                await store.ReindexOcRolesAsync(guildId, user.Id);
                var character = await store.GetAsync(guildId, user.Id, characterName);
                var defaultProperties = await settings.GetDefaultPropertiesAsync(guildId);
                var ocRoleIds = await settings.GetOcRoleIdsAsync(guildId);
                await command.RespondAsync(character is null
                    ? "Character not found."
                    : Format(character, user, defaultProperties, ocRoleIds), ephemeral: true);
                break;
            case "delete":
                if (await store.GetAsync(guildId, user.Id, characterName) is null)
                {
                    await command.RespondAsync("Character not found.", ephemeral: true);
                    break;
                }

                var deleteSession = new DeleteSession(guildId, user.Id, characterName, command);
                FilloutSessions.TryRemove((command.Channel.Id, command.User.Id), out _);
                DeleteSessions[(command.Channel.Id, command.User.Id)] = deleteSession;
                await command.RespondAsync(
                    $"This permanently removes **{characterName}** and all of its stored properties. " +
                    $"To verify, type `confirm {characterName}` in this channel. Type `cancel` to stop. Your reply will be deleted.",
                    ephemeral: true);
                break;
        }
    }

    public static async Task HandleFilloutMessageAsync(
        SocketMessage message,
        CharacterStore store,
        CharacterRoleService characterRoles)
    {
        if (message.Author.IsBot) return;

        if (DeleteSessions.TryGetValue((message.Channel.Id, message.Author.Id), out var deletion))
        {
            await HandleDeleteConfirmationAsync(message, store, characterRoles, deletion);
            return;
        }

        if (!FilloutSessions.TryGetValue((message.Channel.Id, message.Author.Id), out var session))
            return;

        var reply = message.Content.Trim();
        if (reply.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            FilloutSessions.TryRemove((message.Channel.Id, message.Author.Id), out _);
            await DeleteReplyAsync(message);
            await UpdatePromptAsync(session, $"Fillout ended. **{session.CharacterName}** was saved with the values entered so far.");
            return;
        }

        await DeleteReplyAsync(message);

        var field = session.Fields[session.FieldIndex].Field;
        if (!string.IsNullOrWhiteSpace(reply) && !reply.Equals("skip", StringComparison.OrdinalIgnoreCase))
            await store.SetFieldAsync(session.GuildId, session.OwnerId, session.CharacterName, field, reply);

        session.FieldIndex++;
        if (session.FieldIndex >= session.Fields.Length)
        {
            FilloutSessions.TryRemove((message.Channel.Id, message.Author.Id), out _);
            await UpdatePromptAsync(session, $"Fillout complete. **{session.CharacterName}** is ready. Use `/character view` to review it.");
            return;
        }

        await UpdatePromptAsync(session, PromptFor(session));
    }

    private static SocketSlashCommandDataOption Option(
        IReadOnlyCollection<SocketSlashCommandDataOption> options,
        string name) => options.First(option => option.Name == name);

    private static string? OptionalString(IReadOnlyCollection<SocketSlashCommandDataOption> options, string name) =>
        options.FirstOrDefault(option => option.Name == name)?.Value as string;

    private static SlashCommandOptionBuilder AutocompleteOption(string name, string description, bool required = true) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(required)
            .WithAutocomplete(true);

    private static ulong? ReadUserId(IReadOnlyCollection<AutocompleteOption> options)
    {
        var value = options.FirstOrDefault(option => option.Name == "user")?.Value;
        if (value is IUser user) return user.Id;
        return ulong.TryParse(value?.ToString(), out var id) ? id : null;
    }

    private static string Format(
        Character character,
        IUser owner,
        IReadOnlyList<string> defaultProperties,
        IReadOnlyList<ulong> ocRoleIds)
    {
        var text = new StringBuilder($"**{character.Name}** — {owner.Mention}\n");
        if (character.OcRoleIndex > 0)
        {
            var role = character.OcRoleIndex <= ocRoleIds.Count
                ? $" — <@&{ocRoleIds[character.OcRoleIndex - 1]}>"
                : " — no configured role at this index";
            text.AppendLine($"OC role index: **{character.OcRoleIndex}**{role}");
        }
        foreach (var property in defaultProperties)
            text.AppendLine($"{CharacterSchema.Label(property)}: {CharacterValue(character, property) ?? "—"}");

        var defaults = defaultProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in StoredProperties(character).Where(item => !defaults.Contains(item.Key)))
            text.AppendLine($"{CharacterSchema.Label(property.Key)}: {property.Value}");
        return text.ToString();
    }

    private static string? CharacterValue(Character character, string property)
    {
        var normalized = CharacterSchema.Normalize(property);
        var builtIn = normalized switch
        {
            "age" => character.Age,
            "gender" => character.Gender,
            "region" => character.Region,
            "occupation" => character.Occupation,
            "aliases" or "alias" => character.Aliases.Count == 0 ? null : string.Join(", ", character.Aliases),
            "reference" => character.CharacterReference.Value,
            "reference-kind" => character.CharacterReference.Kind,
            "reference-format" => character.CharacterReference.Format,
            _ => null
        };
        if (builtIn is not null) return builtIn;

        var custom = character.AdditionalProperties.FirstOrDefault(item =>
            CharacterSchema.Normalize(item.Key) == normalized);
        return custom.Key is null ? null : custom.Value.ToString();
    }

    private static IEnumerable<KeyValuePair<string, string>> StoredProperties(Character character)
    {
        if (character.Age is not null) yield return new("age", character.Age);
        if (character.Gender is not null) yield return new("gender", character.Gender);
        if (character.Region is not null) yield return new("region", character.Region);
        if (character.Occupation is not null) yield return new("occupation", character.Occupation);
        if (character.Aliases.Count > 0) yield return new("aliases", string.Join(", ", character.Aliases));
        if (character.CharacterReference.Value is not null) yield return new("reference", character.CharacterReference.Value);
        foreach (var item in character.AdditionalProperties) yield return new(item.Key, item.Value.ToString());
    }

    private static string PromptFor(FilloutSession session) =>
        $"**{session.Fields[session.FieldIndex].Label}?**" +
        (session.Fields[session.FieldIndex].Suggestions.Count > 0
            ? $" Suggestions: {string.Join(", ", session.Fields[session.FieldIndex].Suggestions.Select(value => $"`{value}`"))}."
            : string.Empty) +
        " Reply in this channel with a value, `skip` to leave it empty, or `stop`/`end` to finish now. Your reply will be deleted immediately.";

    private static async Task UpdatePromptAsync(FilloutSession session, string content) =>
        await session.Interaction.ModifyOriginalResponseAsync(message => message.Content = content);

    private static async Task DeleteReplyAsync(SocketMessage message)
    {
        try
        {
            await message.DeleteAsync();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not delete character-fillout reply: {exception.Message}");
        }
    }

    private static async Task HandleDeleteConfirmationAsync(
        SocketMessage message,
        CharacterStore store,
        CharacterRoleService characterRoles,
        DeleteSession session)
    {
        var reply = message.Content.Trim();
        await DeleteReplyAsync(message);

        if (reply.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
            reply.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            DeleteSessions.TryRemove((message.Channel.Id, message.Author.Id), out _);
            await session.Interaction.ModifyOriginalResponseAsync(properties => properties.Content = "Character deletion cancelled.");
            return;
        }

        var expected = $"confirm {session.CharacterName}";
        if (!reply.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            await session.Interaction.ModifyOriginalResponseAsync(properties =>
                properties.Content = $"Confirmation did not match. Type exactly `confirm {session.CharacterName}` or type `cancel`.");
            return;
        }

        var deleted = await store.DeleteAsync(session.GuildId, session.OwnerId, session.CharacterName);
        var roleSync = deleted
            ? await characterRoles.SyncMemberAsync(session.GuildId, session.OwnerId)
            : null;
        DeleteSessions.TryRemove((message.Channel.Id, message.Author.Id), out _);
        await session.Interaction.ModifyOriginalResponseAsync(properties => properties.Content = deleted
            ? $"**{session.CharacterName}** and all of its stored data were permanently deleted." +
              (roleSync!.Success
                  ? " Remaining characters and OC roles were shifted down sequentially."
                  : $" Character indexes were compacted, but Discord roles could not be fully synchronized: {roleSync.Error}")
            : "The character no longer exists.");
    }

    private static Task UpdateOriginalAsync(SocketSlashCommand command, string content) =>
        command.ModifyOriginalResponseAsync(properties => properties.Content = content);

    private static string RoleNotice(CharacterRoleSyncResult result)
    {
        if (!result.Success)
            return $" Character index **{result.CharacterCount}** was saved, but its Discord approval roles could not be synchronized: {result.Error}";
        if (result.RoleCapacity == 0) return string.Empty;
        return result.AssignedRoleId is { } roleId
            ? $" Assigned OC role **#{result.CharacterCount}**: <@&{roleId}>."
            : string.Empty;
    }

    private static async Task<ApprovalMessageDelivery> SendApprovalMessagesAsync(
        SocketSlashCommand command,
        CharacterSettingsStore settings,
        IUser user,
        string characterName)
    {
        if (command.GuildId is not { } guildId || command.Channel is not SocketGuildChannel commandChannel)
            return new ApprovalMessageDelivery(0, 0);

        var configuredMessages = await settings.GetApprovalMessagesAsync(guildId);
        var sent = 0;
        var failed = 0;
        foreach (var configured in configuredMessages)
        {
            var content = configured.Template
                .Replace("@{user}", user.Mention, StringComparison.OrdinalIgnoreCase)
                .Replace("{user}", user.Mention, StringComparison.OrdinalIgnoreCase)
                .Replace("{charactername}", characterName, StringComparison.OrdinalIgnoreCase);
            try
            {
                if (configured.Destination.Equals("dm", StringComparison.OrdinalIgnoreCase))
                {
                    var directMessage = await user.CreateDMChannelAsync();
                    await directMessage.SendMessageAsync(content);
                }
                else if (configured.Destination.Equals("here", StringComparison.OrdinalIgnoreCase))
                {
                    await command.Channel.SendMessageAsync(content);
                }
                else if (configured.ChannelId is { } destinationId &&
                         commandChannel.Guild.GetChannel(destinationId) is IMessageChannel destination)
                {
                    await destination.SendMessageAsync(content);
                }
                else
                {
                    failed++;
                    continue;
                }
                sent++;
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"Could not send character approval message for {user}: {exception}");
            }
        }
        return new ApprovalMessageDelivery(sent, failed);
    }

    private static string ApprovalMessageNotice(ApprovalMessageDelivery delivery)
    {
        if (delivery.Sent == 0 && delivery.Failed == 0) return string.Empty;
        if (delivery.Failed == 0)
            return $" Sent **{delivery.Sent}** configured approval message(s).";
        return $" Sent **{delivery.Sent}** approval message(s); **{delivery.Failed}** could not be delivered.";
    }

    private sealed record FilloutSession(
        ulong GuildId,
        ulong OwnerId,
        string CharacterName,
        FilloutField[] Fields,
        SocketSlashCommand Interaction)
    {
        public int FieldIndex { get; set; }
    }

    private sealed record DeleteSession(
        ulong GuildId,
        ulong OwnerId,
        string CharacterName,
        SocketSlashCommand Interaction);

    private sealed record FilloutField(string Field, string Label, IReadOnlyList<string> Suggestions);

    private sealed record ApprovalMessageDelivery(int Sent, int Failed);
}
