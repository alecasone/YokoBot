namespace Yoko.Bot.Models;

internal sealed class MemberAlertGuildSettings
{
    public Dictionary<string, RoleLeaveAlert> LeaveAlerts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public NewAccountAlert? NewAccountAlert { get; set; }
}

internal sealed class RoleLeaveAlert
{
    public ulong RoleId { get; set; }
    public AlertDestination Destination { get; set; } = new();
    public string MessageTemplate { get; set; } = string.Empty;
}

internal sealed class NewAccountAlert
{
    public int MaximumAccountAgeDays { get; set; }
    public AlertDestination Destination { get; set; } = new();
    public string MessageTemplate { get; set; } = string.Empty;
}

internal sealed class AlertDestination
{
    // channel: TargetId is the destination channel.
    // user-dm: TargetId is the fixed DM recipient.
    // subject-dm: the member who triggered the alert receives the DM.
    public string Kind { get; set; } = "channel";
    public ulong? TargetId { get; set; }
}
