namespace Yoko.Bot.Models;

internal sealed class ModeratedUser
{
    public DateTimeOffset JoinedAt { get; set; }
    public bool Verified { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public Dictionary<string, DateTimeOffset> RuleExecutions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class AutoModerationGuildRules
{
    public string UnverifiedRoleName { get; set; } = "Unverified";
    public Dictionary<string, AutoModerationRule> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PendingModerationAction> PendingActions { get; set; } = [];
}

internal sealed class AutoModerationRule
{
    public required string Title { get; set; }
    public string Type { get; set; } = "time-warn";
    public string Condition { get; set; } = "unverified";
    public double DelayHours { get; set; }
    public string MessageDestination { get; set; } = "none";
    public ulong? MessageChannelId { get; set; }
    public string? MessageTemplate { get; set; }
    public string Action { get; set; } = "none";
    public double? MuteHours { get; set; }
    public bool NeedApproval { get; set; }
    public ulong? ApprovalChannelId { get; set; }
    public string? ApprovalMessageTemplate { get; set; }
}

internal sealed class PendingModerationAction
{
    public ulong ApprovalMessageId { get; set; }
    public ulong ApprovalChannelId { get; set; }
    public ulong UserId { get; set; }
    public required string RuleTitle { get; set; }
    public required string Action { get; set; }
    public double? MuteHours { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
