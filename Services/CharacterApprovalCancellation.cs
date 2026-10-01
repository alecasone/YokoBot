using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class CharacterApprovalCancellation(
    CharacterStore characters, RelationshipStore relationships, SceneStore scenes,
    Func<ulong, ulong, Task<CharacterRoleSyncResult>> syncRoles,
    Func<ulong, Task> queueSite)
{
    public async Task<IReadOnlyList<string>> AbortAsync(ulong guildId, ulong ownerId, Guid characterId)
    {
        var selector = CharacterSchema.Selector(characterId);
        var character = await characters.GetAsync(guildId, ownerId, selector);
        // Resolve legacy scene names before removing the character.
        await scenes.GetAllAsync(guildId);
        await characters.DeleteAsync(guildId, ownerId, selector);
        var warnings = new List<string>();
        async Task Attempt(string step, Func<Task> action)
        {
            try { await action(); }
            catch (Exception exception)
            {
                warnings.Add(step + " failed; staff must check the bot log.");
                Console.Error.WriteLine($"Character approval abort ({guildId}, {characterId}), {step}: {exception}");
            }
        }
        await Attempt("Relationship cleanup", async () =>
            await relationships.RemoveForCharacterAsync(guildId, characterId));
        if (character is not null)
            await Attempt("Scene cleanup", async () =>
                await scenes.RemoveCharactersAsync(guildId, [new OwnedCharacter(ownerId, character)]));
        await Attempt("Website update queue", () => queueSite(guildId));
        await Attempt("OC role sync", async () =>
        {
            var result = await syncRoles(guildId, ownerId);
            if (!result.Success) throw new InvalidOperationException(result.Error);
        });
        return warnings;
    }
}