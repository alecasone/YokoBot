namespace Yoko.Bot.Models;

internal sealed class ScheduledDestination
{
    // channel: TargetId belongs to the guild that owns the schedule.
    // user-dm: TargetId is a member of that guild.
    public string Kind { get; set; } = "channel";
    public ulong TargetId { get; set; }
}

internal sealed class AutoMessageSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ScheduledDestination Destination { get; set; } = new();
    public TimeSpan Interval { get; set; }
    public string Mode { get; set; } = "pseudorandom";
    public List<string> Messages { get; set; } = [];
    public bool Paused { get; set; }
    public DateTimeOffset? NextDueAt { get; set; }
    public int NextIndex { get; set; }
    public List<int> ShuffleBag { get; set; } = [];
    public int? LastIndex { get; set; }
    public string? LastError { get; set; }
}

internal sealed class ReminderSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ScheduledDestination Destination { get; set; } = new();
    public TimeSpan Delay { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset? NextDueAt { get; set; }
    public string? LastError { get; set; }
}

internal sealed class ScheduledMessageGuildSettings
{
    public List<AutoMessageSchedule> AutoMessages { get; set; } = [];
    public List<ReminderSchedule> Reminders { get; set; } = [];
}

internal sealed record ScheduledDelivery(
    ulong GuildId,
    string ScheduleId,
    string Name,
    bool IsReminder,
    ScheduledDestination Destination,
    string Message);
