using System.Collections.Concurrent;
using System.Globalization;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class MemberAlertService
{
    private readonly DiscordSocketClient _client;
    private readonly MemberAlertStore _store;
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), MemberSnapshot> _members = new();

    public MemberAlertService(DiscordSocketClient client, MemberAlertStore store)
    {
        _client = client;
        _store = store;
    }

    public Task StartAsync()
    {
        foreach (var guild in _client.Guilds)
            foreach (var member in guild.Users.Where(member => !member.IsBot))
                Remember(member);
        return Task.CompletedTask;
    }

    public async Task HandleUserJoinedAsync(SocketGuildUser user)
    {
        if (user.IsBot) return;
        Remember(user);

        var alert = (await _store.GetAsync(user.Guild.Id)).NewAccountAlert;
        if (alert is null) return;
        var accountAge = DateTimeOffset.UtcNow - user.CreatedAt;
        if (accountAge < TimeSpan.Zero) accountAge = TimeSpan.Zero;
        if (accountAge >= TimeSpan.FromDays(alert.MaximumAccountAgeDays)) return;

        await DeliverAsync(
            user.Guild,
            user,
            user.DisplayName,
            user.JoinedAt,
            alert.Destination,
            alert.MessageTemplate,
            role: null,
            accountAge,
            alert.MaximumAccountAgeDays,
            "new-account");
    }

    public Task HandleGuildMemberUpdatedAsync(
        Cacheable<SocketGuildUser, ulong> before,
        SocketGuildUser after)
    {
        if (!after.IsBot) Remember(after);
        return Task.CompletedTask;
    }

    public async Task HandleUserLeftAsync(SocketGuild guild, SocketUser user)
    {
        if (user.IsBot) return;
        _members.TryRemove((guild.Id, user.Id), out var snapshot);
        snapshot ??= user is SocketGuildUser guildUser ? Capture(guildUser) : MemberSnapshot.Empty(user.Username);

        var settings = await _store.GetAsync(guild.Id);
        foreach (var alert in settings.LeaveAlerts.Values
                     .Where(alert => snapshot.RoleIds.Contains(alert.RoleId))
                     .OrderBy(alert => alert.RoleId))
        {
            await DeliverAsync(
                guild,
                user,
                snapshot.DisplayName,
                snapshot.JoinedAt,
                alert.Destination,
                alert.MessageTemplate,
                guild.GetRole(alert.RoleId),
                accountAge: null,
                thresholdDays: null,
                "role-leave");
        }
    }

    private void Remember(SocketGuildUser member) =>
        _members[(member.Guild.Id, member.Id)] = Capture(member);

    private static MemberSnapshot Capture(SocketGuildUser member) => new(
        member.Roles.Select(role => role.Id).ToHashSet(),
        member.DisplayName,
        member.JoinedAt);

    private async Task DeliverAsync(
        SocketGuild guild,
        SocketUser subject,
        string displayName,
        DateTimeOffset? joinedAt,
        AlertDestination destination,
        string template,
        IRole? role,
        TimeSpan? accountAge,
        int? thresholdDays,
        string alertType)
    {
        var content = Render(
            template,
            guild,
            subject,
            displayName,
            joinedAt,
            role,
            accountAge,
            thresholdDays);
        if (content.Length > 2000) content = content[..1999] + "…";

        try
        {
            switch (destination.Kind)
            {
                case "channel" when destination.TargetId is { } channelId &&
                                         guild.GetChannel(channelId) is IMessageChannel channel:
                    await channel.SendMessageAsync(content);
                    break;
                case "subject-dm":
                    await subject.SendMessageAsync(content);
                    break;
                case "user-dm" when destination.TargetId is { } userId &&
                                         (_client.GetUser(userId) ?? guild.GetUser(userId)) is { } recipient:
                    await recipient.SendMessageAsync(content);
                    break;
                default:
                    Console.WriteLine($"Member alert {alertType} for {subject.Id} has an unavailable destination.");
                    break;
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not deliver member alert {alertType} for {subject.Id}: {exception.Message}");
        }
    }

    internal static string Render(
        string template,
        SocketGuild guild,
        SocketUser user,
        string displayName,
        DateTimeOffset? joinedAt,
        IRole? role,
        TimeSpan? accountAge,
        int? thresholdDays)
    {
        var age = accountAge is { } value ? FormatAge(value) : "unknown";
        var ageDays = accountAge is { } days
            ? days.TotalDays.ToString("0.0", CultureInfo.InvariantCulture)
            : "unknown";
        return template
            .Replace("@{user}", user.Mention, StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", user.Mention, StringComparison.OrdinalIgnoreCase)
            .Replace("{username}", user.Username, StringComparison.OrdinalIgnoreCase)
            .Replace("{displayname}", displayName, StringComparison.OrdinalIgnoreCase)
            .Replace("{userid}", user.Id.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{server}", guild.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{role}", role?.Mention ?? "unknown role", StringComparison.OrdinalIgnoreCase)
            .Replace("{rolename}", role?.Name ?? "unknown role", StringComparison.OrdinalIgnoreCase)
            .Replace("{accountage}", age, StringComparison.OrdinalIgnoreCase)
            .Replace("{accountagedays}", ageDays, StringComparison.OrdinalIgnoreCase)
            .Replace("{threshold}", thresholdDays is { } threshold ? $"{threshold} days" : "unknown", StringComparison.OrdinalIgnoreCase)
            .Replace("{createdat}", DiscordTimestamp(user.CreatedAt), StringComparison.OrdinalIgnoreCase)
            .Replace("{joinedat}", joinedAt is { } joined ? DiscordTimestamp(joined) : "unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1) return $"{(int)age.TotalDays}d {age.Hours}h";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours}h {age.Minutes}m";
        return $"{Math.Max(0, (int)age.TotalMinutes)}m";
    }

    private static string DiscordTimestamp(DateTimeOffset timestamp) =>
        $"<t:{timestamp.ToUnixTimeSeconds()}:F>";

    private sealed record MemberSnapshot(
        HashSet<ulong> RoleIds,
        string DisplayName,
        DateTimeOffset? JoinedAt)
    {
        public static MemberSnapshot Empty(string username) => new([], username, null);
    }
}
