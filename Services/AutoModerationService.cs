using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class AutoModerationService
{
    private static readonly TimeSpan AuditInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VerificationRecheckInterval = TimeSpan.FromDays(1);

    private readonly DiscordSocketClient _client;
    private readonly UserStore _users;
    private readonly AutoModerationRuleStore _rules;
    private readonly CancellationTokenSource _stopping = new();
    private int _started;

    public AutoModerationService(DiscordSocketClient client, UserStore users, AutoModerationRuleStore rules)
    {
        _client = client;
        _users = users;
        _rules = rules;
    }

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        foreach (var guild in _client.Guilds) await SyncKnownMembersAsync(guild);
        foreach (var guild in _client.Guilds) await RecheckVerificationAsync(guild.Id);
        await AuditAsync();
        _ = RunAuditLoopAsync();
        _ = RunVerificationRecheckLoopAsync();
    }

    public void Stop() => _stopping.Cancel();

    public async Task HandleUserJoinedAsync(SocketGuildUser user)
    {
        if (user.IsBot) return;
        await _users.RegisterJoinAsync(user.Guild.Id, user.Id, user.JoinedAt ?? DateTimeOffset.UtcNow);
        try
        {
            var settings = await _rules.GetAsync(user.Guild.Id);
            IRole? role = user.Guild.Roles.FirstOrDefault(item =>
                item.Name.Equals(settings.UnverifiedRoleName, StringComparison.OrdinalIgnoreCase));
            role ??= await user.Guild.CreateRoleAsync(settings.UnverifiedRoleName, GuildPermissions.None);
            await user.AddRoleAsync(role);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not assign the Unverified role to {user}: {exception.Message}");
        }
    }

    public Task RecordMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot || message.Channel is not SocketGuildChannel channel) return Task.CompletedTask;
        return _users.RecordMessageAsync(channel.Guild.Id, message.Author.Id, message.Timestamp);
    }

    public Task MarkVerifiedAsync(ulong guildId, ulong userId) =>
        _users.MarkVerifiedAsync(guildId, userId, DateTimeOffset.UtcNow);

    public async Task<VerificationRecheckResult> RecheckVerificationAsync(ulong guildId)
    {
        var guild = _client.GetGuild(guildId);
        if (guild is null) return VerificationRecheckResult.Empty;

        var storedUsers = await _users.GetUsersAsync(guildId);
        var ruleSettings = await _rules.GetAsync(guildId);
        var verifiedRole = guild.Roles.FirstOrDefault(role =>
            role.Name.Equals("Verified", StringComparison.OrdinalIgnoreCase));
        var unverifiedRole = guild.Roles.FirstOrDefault(role =>
            role.Name.Equals(ruleSettings.UnverifiedRoleName, StringComparison.OrdinalIgnoreCase));

        var roleVerifiedIds = new List<ulong>();
        var roleUnverifiedIds = new List<ulong>();
        var missingMembers = 0;
        var conflicts = 0;
        var noStatusRole = 0;
        foreach (var (userId, _) in storedUsers)
        {
            var member = guild.GetUser(userId);
            if (member is null)
            {
                missingMembers++;
                continue;
            }
            var hasVerified = verifiedRole is not null && member.Roles.Contains(verifiedRole);
            var hasUnverified = unverifiedRole is not null && member.Roles.Contains(unverifiedRole);
            if (hasVerified && hasUnverified) conflicts++;
            else if (hasVerified) roleVerifiedIds.Add(userId);
            else if (hasUnverified) roleUnverifiedIds.Add(userId);
            else noStatusRole++;
        }

        var changes = await _users.ReconcileVerificationStatesAsync(
            guildId,
            roleVerifiedIds,
            roleUnverifiedIds,
            DateTimeOffset.UtcNow);
        return new VerificationRecheckResult(
            storedUsers.Count,
            roleVerifiedIds.Count,
            roleUnverifiedIds.Count,
            changes.Promoted,
            changes.Demoted,
            conflicts,
            noStatusRole,
            missingMembers,
            verifiedRole is not null,
            unverifiedRole is not null);
    }

    public async Task<bool> HandleApprovalMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot || message.Channel is not SocketGuildChannel channel ||
            message is not SocketUserMessage userMessage)
            return false;

        ulong? referencedMessageId = null;
        if (userMessage.Reference?.MessageId.IsSpecified == true)
            referencedMessageId = userMessage.Reference.MessageId.Value;
        else if (userMessage.ReferencedMessage is not null)
            referencedMessageId = userMessage.ReferencedMessage.Id;
        if (referencedMessageId is null) return false;

        var normalized = message.Content.Trim().TrimEnd('.', '!');
        var confirm = normalized.Equals("Confirm, Yoko", StringComparison.OrdinalIgnoreCase) ||
                      normalized.Equals("Confirm Yoko", StringComparison.OrdinalIgnoreCase) ||
                      normalized.Equals("Approve, Yoko", StringComparison.OrdinalIgnoreCase);
        var cancel = normalized.Equals("Cancel, Yoko", StringComparison.OrdinalIgnoreCase) ||
                     normalized.Equals("Cancel Yoko", StringComparison.OrdinalIgnoreCase) ||
                     normalized.Equals("Reject, Yoko", StringComparison.OrdinalIgnoreCase);
        if (!confirm && !cancel) return false;

        if (message.Author is not SocketGuildUser admin || !admin.GuildPermissions.Administrator)
        {
            await message.Channel.SendMessageAsync("Only an administrator can approve this moderation action.");
            return true;
        }

        var pending = await _rules.GetPendingAsync(channel.Guild.Id, referencedMessageId.Value);
        if (pending is null) return false;

        var approvalMessage = userMessage.ReferencedMessage ??
                              await message.Channel.GetMessageAsync(referencedMessageId.Value) as IUserMessage;

        try { await message.DeleteAsync(); } catch { }
        if (cancel)
        {
            await _rules.RemovePendingAsync(channel.Guild.Id, pending.ApprovalMessageId);
            if (approvalMessage is not null)
                await UpdateApprovalMessageAsync(approvalMessage, $"~~{approvalMessage.Content}~~\nCancelled by {admin.Mention}.");
            return true;
        }

        var member = channel.Guild.GetUser(pending.UserId);
        var success = member is not null && await ExecuteActionAsync(channel.Guild, member, pending.Action, pending.MuteHours, pending.RuleTitle);
        if (success)
        {
            await _rules.RemovePendingAsync(channel.Guild.Id, pending.ApprovalMessageId);
            if (approvalMessage is not null)
                await UpdateApprovalMessageAsync(approvalMessage, $"{approvalMessage.Content}\nApproved by {admin.Mention} — action completed.");
        }
        else
        {
            if (approvalMessage is not null)
                await UpdateApprovalMessageAsync(approvalMessage, $"{approvalMessage.Content}\nApproval by {admin.Mention} failed; check permissions and role hierarchy.");
        }
        return true;
    }

    private async Task RunAuditLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(AuditInterval);
            while (await timer.WaitForNextTickAsync(_stopping.Token)) await AuditAsync();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception exception) { Console.WriteLine($"Auto-moderation audit stopped unexpectedly: {exception}"); }
    }

    private async Task RunVerificationRecheckLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(VerificationRecheckInterval);
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                foreach (var guild in _client.Guilds)
                    await RecheckVerificationAsync(guild.Id);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception exception) { Console.WriteLine($"Verification recheck stopped unexpectedly: {exception}"); }
    }

    private async Task AuditAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var guild in _client.Guilds)
        {
            var settings = await _rules.GetAsync(guild.Id);
            var users = await _users.GetUsersAsync(guild.Id);
            foreach (var (userId, state) in users)
            {
                var member = guild.GetUser(userId);
                if (member is null || member.IsBot) continue;
                foreach (var rule in settings.Rules.Values.Where(rule => rule.Type == "time-warn"))
                {
                    if (!IsDue(rule, state, now)) continue;
                    var completed = await ExecuteRuleAsync(guild, member, rule);
                    if (completed) await _users.MarkRuleExecutedAsync(guild.Id, userId, rule.Title, now);
                    if (completed && rule.Action is "kick" or "ban" && !rule.NeedApproval) break;
                }
            }
        }
    }

    private static bool IsDue(AutoModerationRule rule, ModeratedUser state, DateTimeOffset now)
    {
        DateTimeOffset basis;
        switch (rule.Condition)
        {
            case "unverified" when !state.Verified:
                basis = state.JoinedAt;
                if (state.RuleExecutions.ContainsKey(rule.Title)) return false;
                break;
            case "inactive":
                basis = state.LastMessageAt;
                if (state.RuleExecutions.TryGetValue(rule.Title, out var executedAt) && executedAt >= basis) return false;
                break;
            default:
                return false;
        }
        return now - basis >= TimeSpan.FromHours(rule.DelayHours);
    }

    private async Task<bool> ExecuteRuleAsync(SocketGuild guild, SocketGuildUser user, AutoModerationRule rule)
    {
        var renderedUserMessage = await SendUserMessageAsync(guild, user, rule);
        if (rule.Action == "none") return true;

        if (rule.NeedApproval)
        {
            if (rule.ApprovalChannelId is not { } approvalChannelId || guild.GetTextChannel(approvalChannelId) is not { } approvalChannel)
            {
                Console.WriteLine($"Rule {rule.Title} needs approval but its approval channel is unavailable.");
                return false;
            }

            var template = rule.ApprovalMessageTemplate ?? "**{automod}** requests `{action}` for {user}. {message}";
            var content = Render(template, user, rule, renderedUserMessage ?? "No user message was sent.") +
                          "\n\nReply to this message with `Execute, Yoko.` or `Cancel, Yoko.`";
            try
            {
                var approvalMessage = await approvalChannel.SendMessageAsync(content);
                await _rules.AddPendingAsync(guild.Id, new PendingModerationAction
                {
                    ApprovalMessageId = approvalMessage.Id,
                    ApprovalChannelId = approvalChannel.Id,
                    UserId = user.Id,
                    RuleTitle = rule.Title,
                    Action = rule.Action,
                    MuteHours = rule.MuteHours
                });
                return true;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Could not queue approval for rule {rule.Title}: {exception.Message}");
                return false;
            }
        }

        return await ExecuteActionAsync(guild, user, rule.Action, rule.MuteHours, rule.Title);
    }

    private static async Task<string?> SendUserMessageAsync(SocketGuild guild, SocketGuildUser user, AutoModerationRule rule)
    {
        if (rule.MessageDestination == "none" || string.IsNullOrWhiteSpace(rule.MessageTemplate)) return null;
        var content = Render(rule.MessageTemplate, user, rule, string.Empty);
        try
        {
            if (rule.MessageDestination == "dm-user") await user.SendMessageAsync(content);
            else if (rule.MessageChannelId is { } channelId && guild.GetTextChannel(channelId) is { } channel)
                await channel.SendMessageAsync(content);
            else return null;
            return content;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not send message for rule {rule.Title}: {exception.Message}");
            return null;
        }
    }

    private async Task<bool> ExecuteActionAsync(
        SocketGuild guild,
        SocketGuildUser user,
        string action,
        double? muteHours,
        string ruleTitle)
    {
        try
        {
            switch (action)
            {
                case "kick":
                    await user.KickAsync($"Auto-moderation rule: {ruleTitle}");
                    await _users.RemoveUserAsync(guild.Id, user.Id);
                    break;
                case "ban":
                    await guild.AddBanAsync(user, 0, $"Auto-moderation rule: {ruleTitle}");
                    await _users.RemoveUserAsync(guild.Id, user.Id);
                    break;
                case "mute":
                    await user.SetTimeOutAsync(TimeSpan.FromHours(muteHours ?? 1));
                    break;
                case "none":
                    break;
                default:
                    return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not execute {action} for rule {ruleTitle} on {user}: {exception.Message}");
            return false;
        }
    }

    private async Task SyncKnownMembersAsync(SocketGuild guild)
    {
        var settings = await _rules.GetAsync(guild.Id);
        var unverifiedRole = guild.Roles.FirstOrDefault(item =>
            item.Name.Equals(settings.UnverifiedRoleName, StringComparison.OrdinalIgnoreCase));
        var members = guild.Users.Where(member => !member.IsBot).Select(member => (
            member.Id,
            member.JoinedAt ?? DateTimeOffset.UtcNow,
            unverifiedRole is null || !member.Roles.Contains(unverifiedRole)));
        await _users.EnsureUsersAsync(guild.Id, members);
    }

    private static string Render(string template, SocketGuildUser user, AutoModerationRule rule, string message) =>
        template
            .Replace("@{user}", user.Mention, StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", user.Mention, StringComparison.OrdinalIgnoreCase)
            .Replace("{automod title}", rule.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{automod}", rule.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", rule.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{action}", rule.Action, StringComparison.OrdinalIgnoreCase)
            .Replace("{message}", message, StringComparison.OrdinalIgnoreCase);

    private static Task UpdateApprovalMessageAsync(IUserMessage message, string content) =>
        message.ModifyAsync(properties => properties.Content = content);
}

internal sealed record VerificationRecheckResult(
    int Scanned,
    int MembersWithVerifiedRole,
    int MembersWithUnverifiedRole,
    int Promoted,
    int Demoted,
    int Conflicts,
    int NoStatusRole,
    int MissingMembers,
    bool VerifiedRoleFound,
    bool UnverifiedRoleFound)
{
    public static VerificationRecheckResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, false, false);
}
