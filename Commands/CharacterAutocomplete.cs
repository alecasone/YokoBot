using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static class CharacterAutocomplete
{
    public static async Task RespondAsync(
        SocketAutocompleteInteraction interaction,
        CharacterStore characters,
        ulong guildId,
        ulong userId,
        string typed)
    {
        var choices = await characters.GetCharacterChoicesAsync(guildId, userId);
        await interaction.RespondAsync(choices
            .Where(choice => choice.Name.Contains(typed, StringComparison.OrdinalIgnoreCase) ||
                             CharacterSchema.Selector(choice.PublicId).Equals(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(choice => new AutocompleteResult(
                CharacterSchema.BoundedName(choice.Name),
                CharacterSchema.Selector(choice.PublicId))));
    }
}
