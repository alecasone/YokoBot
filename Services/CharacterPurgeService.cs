using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class CharacterPurgeService(
    CharacterStore characters, RelationshipStore relationships, SceneStore scenes,
    Func<ulong, ulong, Task<CharacterRoleSyncResult>> syncRoles,
    Func<ulong, Task> queueSite)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CharacterPurgeResult> PurgeAsync(ulong guildId, ulong? ownerId, IReadOnlyCollection<Guid> confirmedIds)
    {
        await _gate.WaitAsync();
        try
        {
            // Resolve legacy scene references while the characters still exist.
            await scenes.GetAllAsync(guildId);
            var deleted = await characters.DeleteConfirmedScopeAsync(guildId, ownerId, confirmedIds);
            if (deleted is null) return new(true, [], []);
            if (deleted.Count == 0) return new(false, [], []);
            var warnings = new List<string>();
            async Task Attempt(string step, Func<Task> action)
            {
                try { await action(); }
                catch (Exception exception)
                {
                    warnings.Add(step + " failed; staff must check the bot log.");
                    Console.Error.WriteLine($"Character purge ({guildId}), {step}: {exception}");
                }
            }
            await Attempt("Relationship cleanup", async () =>
                await relationships.RemoveForCharactersAsync(guildId, deleted.Select(item => item.Character.PublicId).ToArray()));
            await Attempt("Scene cleanup", async () => await scenes.RemoveCharactersAsync(guildId, deleted));
            // Publish deletion even if Discord role reconciliation is unavailable.
            await Attempt("Website update queue", () => queueSite(guildId));
            foreach (var id in deleted.Select(item => item.OwnerId).Distinct())
                await Attempt($"OC role sync for {id}", async () =>
                {
                    var result = await syncRoles(guildId, id);
                    if (!result.Success) throw new InvalidOperationException(result.Error);
                });
            return new(false, deleted, warnings);
        }
        finally { _gate.Release(); }
    }
}

internal sealed record CharacterPurgeResult(bool RosterChanged, IReadOnlyList<OwnedCharacter> Deleted, IReadOnlyList<string> Warnings);
