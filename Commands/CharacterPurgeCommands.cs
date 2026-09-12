using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static partial class CharacterCommands
{
    private static readonly ConcurrentDictionary<(ulong ChannelId, ulong ActorId), PurgeSession> PurgeSessions = new();

    private static async Task BeginPurgeAsync(SocketSlashCommand command, CharacterStore store)
    {
        await command.DeferAsync(ephemeral: true);
        var guildId = command.GuildId!.Value;
        var subcommand = command.Data.Options.First();
        var ownerId = subcommand.Name == "purge-user" ? ((IUser)Option(subcommand.Options, "user").Value).Id : (ulong?)null;
        var characters = (await store.GetAllOwnedAsync(guildId)).Where(item => ownerId is null || item.OwnerId == ownerId).ToArray();
        if (characters.Length == 0)
        {
            await UpdateOriginalAsync(command, "There are no characters in that scope. Nothing was deleted.");
            return;
        }
        foreach (var entry in PurgeSessions.Where(entry => entry.Value.Confirmation.ExpiresAt <= DateTimeOffset.UtcNow))
            PurgeSessions.TryRemove(entry.Key, out _);
        var confirmation = new CharacterPurgeConfirmation(guildId, command.User.Id, command.Channel.Id, ownerId,
            characters.Select(item => item.Character.PublicId).ToArray(), DateTimeOffset.UtcNow);
        FilloutSessions.TryRemove((command.Channel.Id, command.User.Id), out _);
        DeleteSessions.TryRemove((command.Channel.Id, command.User.Id), out _);
        PurgeSessions[(command.Channel.Id, command.User.Id)] = new(confirmation, command);
        var scope = ownerId is { } id ? $"<@{id}> in this server" : "EVERYONE IN THIS SERVER";
        await UpdateOriginalAsync(command,
            $"**PERMANENT BULK DELETION — {characters.Length} character(s)**\nTarget: {scope}.\n" +
            $"Examples: {string.Join(", ", characters.Take(5).Select(item => CharacterSchema.BoundedName(item.Character.Name)))}\n\n" +
            "This deletes their properties, approved/pending relationships, and scene participation (including history). " +
            "Scenes left empty are removed; other participants' scenes remain. OC roles are reconciled and a website update is queued. " +
            "It does NOT delete Discord chat messages, server settings, other servers, or old GitHub snapshots. There is no undo; back up your data first.\n\n" +
            $"**Step 1 of 2:** type exactly `{confirmation.TargetText}` here. A second confirmation follows. " +
            "Type `cancel` to stop. Expires in 5 minutes; your replies are deleted when permissions allow.");
    }

    private static async Task<bool> HandlePurgeReplyAsync(SocketMessage message, CharacterPurgeService purge, PermissionService permissions)
    {
        if (message.Author.IsBot || message.Channel is not SocketGuildChannel channel ||
            !PurgeSessions.TryGetValue((message.Channel.Id, message.Author.Id), out var session)) return false;
        await session.Gate.WaitAsync();
        try
        {
            var confirmation = session.Confirmation;
            var member = channel.Guild.GetUser(message.Author.Id);
            var authorized = member is not null && await permissions.HasAsync(channel.Guild.Id, member, confirmation.Permission);
            if (!PurgeSessions.TryGetValue((message.Channel.Id, message.Author.Id), out var current) || !ReferenceEquals(current, session)) return false;
            var step = confirmation.Reply(channel.Guild.Id, message.Author.Id, message.Channel.Id,
                message.Content, DateTimeOffset.UtcNow, authorized);
            if (step == PurgeConfirmationStep.WrongActor) return false;
            if (step != PurgeConfirmationStep.Expired) await DeleteReplyAsync(message);
            if (confirmation.Finished)
                ((ICollection<KeyValuePair<(ulong, ulong), PurgeSession>>)PurgeSessions)
                    .Remove(new((message.Channel.Id, message.Author.Id), session));
            if (step == PurgeConfirmationStep.Execute)
            {
                // Display progress before committing any deletion; a dead interaction fails safely.
                await UpdateOriginalAsync(session.Interaction, "Both confirmations accepted. Checking the exact roster and deleting…");
                var result = await purge.PurgeAsync(confirmation.GuildId, confirmation.OwnerId, confirmation.CharacterIds);
                if (result.RosterChanged)
                    await UpdateOriginalAsync(session.Interaction, "The character roster changed while you were confirming. Nothing was deleted. Run the command again to review the new scope.");
                else
                {
                    var deletedIds = result.Deleted.Select(item => item.Character.PublicId).ToHashSet();
                    foreach (var fillout in FilloutSessions.Where(entry => entry.Value.GuildId == confirmation.GuildId &&
                        result.Deleted.Any(item => item.OwnerId == entry.Value.OwnerId && item.Character.Name == entry.Value.CharacterName)))
                        FilloutSessions.TryRemove(fillout.Key, out _);
                    foreach (var deletion in DeleteSessions.Where(entry => entry.Value.GuildId == confirmation.GuildId &&
                        CharacterSchema.TryParseSelector(entry.Value.CharacterSelector, out var id) && deletedIds.Contains(id)))
                        DeleteSessions.TryRemove(deletion.Key, out _);
                    await UpdateOriginalAsync(session.Interaction,
                        $"Deleted **{result.Deleted.Count}** character(s). " +
                        (result.Warnings.Count == 0
                            ? "Relationships and scene entries were cleaned up; OC roles reconciled. Website update queued (or pending manual publish if automatic publishing is off)."
                            : "Some follow-up steps failed.\n" + string.Join("\n", result.Warnings.Take(8))) +
                        "\nNo undo is available except restoring your own backup. Previously published data may remain in Git history or caches.");
                }
            }
            else await UpdateOriginalAsync(session.Interaction, step switch
            {
                PurgeConfirmationStep.Cancelled => "Bulk deletion cancelled. Nothing was deleted.",
                PurgeConfirmationStep.Expired => "Bulk deletion expired, was already handled, or your permission changed. Start again if needed.",
                PurgeConfirmationStep.TargetConfirmed => $"**Step 2 of 2:** permanently delete **{confirmation.CharacterIds.Count}** character(s)? Type exactly `{confirmation.FinalText}` here. `cancel` stops deletion.",
                _ => $"Confirmation did not match. Type exactly `{(confirmation.TargetConfirmed ? confirmation.FinalText : confirmation.TargetText)}` or `cancel`."
            });
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Bulk character deletion failed: {exception}");
            try
            {
                await UpdateOriginalAsync(session.Interaction, "Bulk deletion could not finish. Check the bot log and stored records before trying again; do not assume every cleanup step completed.");
            }
            catch (Exception responseException)
            {
                // An expired/deleted interaction cannot carry a second error response.
                Console.Error.WriteLine($"Could not report bulk deletion failure: {responseException}");
            }
            return true;
        }
        finally { session.Gate.Release(); }
    }

    private sealed record PurgeSession(CharacterPurgeConfirmation Confirmation, SocketSlashCommand Interaction)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
