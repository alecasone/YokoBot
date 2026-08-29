using Discord;
using Discord.WebSocket;

namespace Yoko.Bot.Services;

internal sealed class VerificationService
{
    private readonly DiscordSocketClient _client;
    private readonly VerificationSettingsStore _settings;
    private readonly AutoModerationService _moderation;

    public VerificationService(
        DiscordSocketClient client,
        VerificationSettingsStore settings,
        AutoModerationService moderation)
    {
        _client = client;
        _settings = settings;
        _moderation = moderation;
    }

    public async Task<string> VerifyAsync(ulong guildId, ulong userId, string type)
    {
        var guild = _client.GetGuild(guildId);
        var user = guild?.GetUser(userId);
        if (guild is null || user is null) return "Member not found.";

        var settings = await _settings.GetAsync(guildId);
        var profileEntry = settings.Profiles.FirstOrDefault(item =>
            item.Key.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (profileEntry.Value is null) return "Verification type not found.";
        var profile = profileEntry.Value;
        type = profileEntry.Key;

        try
        {
            var rolesToRemove = profile.RemovedRoleIds
                .Select(guild.GetRole)
                .Where(role => role is not null)
                .Cast<IRole>()
                .ToArray();
            if (rolesToRemove.Length > 0) await user.RemoveRolesAsync(rolesToRemove);

            var rolesToAdd = profile.AddedRoleIds
                .Select(guild.GetRole)
                .Where(role => role is not null)
                .Cast<IRole>()
                .DistinctBy(role => role.Id)
                .ToArray();
            if (rolesToAdd.Length > 0) await user.AddRolesAsync(rolesToAdd);
            await _moderation.MarkVerifiedAsync(guildId, userId);
            await SendSuccessMessageAsync(guild, user, settings);
            return $"Verified {user.Mention} as **{type}**.";
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not apply verification profile {type} to {user}: {exception}");
            return "Verification failed because one or more Discord roles could not be changed. Check the bot role hierarchy and Manage Roles permission.";
        }
    }

    private static async Task SendSuccessMessageAsync(
        SocketGuild guild,
        SocketGuildUser user,
        Yoko.Bot.Models.VerificationGuildSettings settings)
    {
        if (settings.SuccessChannelId is not { } channelId ||
            string.IsNullOrWhiteSpace(settings.SuccessMessage) ||
            guild.GetTextChannel(channelId) is not { } channel)
            return;

        var content = settings.SuccessMessage
            .Replace("@{user}", user.Mention, StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", user.Mention, StringComparison.OrdinalIgnoreCase);
        try
        {
            await channel.SendMessageAsync(content);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not send verification success message in {guild.Name}: {exception.Message}");
        }
    }

}
