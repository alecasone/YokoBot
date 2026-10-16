using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class ScheduledMessageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public ScheduledMessageStore(string filePath) => _filePath = Path.GetFullPath(filePath);

    public Task<IReadOnlyList<AutoMessageSchedule>> GetAutoMessagesAsync(ulong guildId) =>
        ReadAsync<IReadOnlyList<AutoMessageSchedule>>(data =>
            data.TryGetValue(guildId.ToString(), out var guild)
                ? guild.AutoMessages.Select(Clone).ToList()
                : []);

    public Task<AutoMessageSchedule?> GetAutoMessageAsync(ulong guildId, string name) =>
        ReadAsync(data => data.TryGetValue(guildId.ToString(), out var guild) &&
                          FindByName(guild.AutoMessages, name) is { } schedule ? Clone(schedule) : null);

    public Task<bool> CreateAutoMessageAsync(ulong guildId, AutoMessageSchedule schedule) =>
        MutateAsync(data =>
        {
            var copy = Clone(schedule);
            Validate(copy);
            var guild = GetGuild(data, guildId);
            if (FindByName(guild.AutoMessages, copy.Name) is not null || guild.AutoMessages.Any(item => item.Id == copy.Id))
                return (false, false);
            guild.AutoMessages.Add(copy);
            return (true, true);
        });

    public Task<bool> UpdateAutoMessageAsync(ulong guildId, string id, Action<AutoMessageSchedule> update) =>
        MutateAsync(data =>
        {
            if (!data.TryGetValue(guildId.ToString(), out var guild)) return (false, false);
            var index = guild.AutoMessages.FindIndex(schedule => schedule.Id == id);
            if (index < 0) return (false, false);
            var copy = Clone(guild.AutoMessages[index]);
            update(copy);
            if (copy.Id != id) throw new ArgumentException("A schedule's identity cannot be changed.");
            Validate(copy);
            if (guild.AutoMessages.Any(item => item.Id != id && SameName(item.Name, copy.Name)))
                return (false, false);
            guild.AutoMessages[index] = Clone(copy);
            return (true, true);
        });

    public Task<bool> DeleteAutoMessageAsync(ulong guildId, string name) =>
        MutateAsync(data =>
        {
            var removed = data.TryGetValue(guildId.ToString(), out var guild) &&
                          guild.AutoMessages.RemoveAll(schedule => SameName(schedule.Name, name)) > 0;
            return (removed, removed);
        });

    public Task<IReadOnlyList<ReminderSchedule>> GetRemindersAsync(ulong guildId) =>
        ReadAsync<IReadOnlyList<ReminderSchedule>>(data =>
            data.TryGetValue(guildId.ToString(), out var guild)
                ? guild.Reminders.Select(Clone).ToList()
                : []);

    public Task<ReminderSchedule?> GetReminderAsync(ulong guildId, string name) =>
        ReadAsync(data => data.TryGetValue(guildId.ToString(), out var guild) &&
                          FindByName(guild.Reminders, name) is { } schedule ? Clone(schedule) : null);

    public Task<bool> CreateReminderAsync(ulong guildId, ReminderSchedule schedule) =>
        MutateAsync(data =>
        {
            var copy = Clone(schedule);
            Validate(copy);
            var guild = GetGuild(data, guildId);
            if (FindByName(guild.Reminders, copy.Name) is not null || guild.Reminders.Any(item => item.Id == copy.Id))
                return (false, false);
            guild.Reminders.Add(copy);
            return (true, true);
        });

    public Task<bool> UpdateReminderAsync(ulong guildId, string id, Action<ReminderSchedule> update) =>
        MutateAsync(data =>
        {
            if (!data.TryGetValue(guildId.ToString(), out var guild)) return (false, false);
            var index = guild.Reminders.FindIndex(schedule => schedule.Id == id);
            if (index < 0) return (false, false);
            var copy = Clone(guild.Reminders[index]);
            update(copy);
            if (copy.Id != id) throw new ArgumentException("A schedule's identity cannot be changed.");
            Validate(copy);
            if (guild.Reminders.Any(item => item.Id != id && SameName(item.Name, copy.Name)))
                return (false, false);
            guild.Reminders[index] = Clone(copy);
            return (true, true);
        });

    public Task<bool> DeleteReminderAsync(ulong guildId, string name) =>
        MutateAsync(data =>
        {
            var removed = data.TryGetValue(guildId.ToString(), out var guild) &&
                          guild.Reminders.RemoveAll(schedule => SameName(schedule.Name, name)) > 0;
            return (removed, removed);
        });

    // Claims are committed before delivery. An uncertain network failure must not cause
    // an automatic duplicate send; reminders can be explicitly armed again by name.
    public Task<IReadOnlyList<ScheduledDelivery>> ClaimDueAsync(
        DateTimeOffset now, Random? random = null, int maximumCount = int.MaxValue) =>
        MutateAsync<IReadOnlyList<ScheduledDelivery>>(data =>
        {
            if (maximumCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumCount));
            var deliveries = new List<ScheduledDelivery>();
            var changed = false;
            random ??= Random.Shared;
            foreach (var (guildKey, guild) in data)
            {
                if (!ulong.TryParse(guildKey, out var guildId) || guildId == 0) continue;
                foreach (var schedule in guild.AutoMessages)
                {
                    if (schedule.Paused || schedule.Messages.Count == 0 ||
                        schedule.NextDueAt is not { } dueAt || dueAt > now) continue;
                    changed = true;
                    try
                    {
                        Validate(schedule);
                        var index = SelectMessageIndex(schedule, random);
                        schedule.NextDueAt = now.Add(schedule.Interval);
                        schedule.LastError = null;
                        deliveries.Add(new ScheduledDelivery(guildId, schedule.Id, schedule.Name,
                            false, Clone(schedule.Destination), schedule.Messages[index]));
                    }
                    catch (Exception exception) when (exception is ArgumentException or OverflowException)
                    {
                        schedule.Paused = true;
                        schedule.NextDueAt = null;
                        schedule.LastError = exception.Message;
                    }
                    if (deliveries.Count >= maximumCount) return (deliveries, changed);
                }
                foreach (var schedule in guild.Reminders)
                {
                    if (schedule.NextDueAt is not { } dueAt || dueAt > now) continue;
                    changed = true;
                    schedule.NextDueAt = null;
                    try
                    {
                        Validate(schedule);
                        schedule.LastError = null;
                        deliveries.Add(new ScheduledDelivery(guildId, schedule.Id, schedule.Name,
                            true, Clone(schedule.Destination), schedule.Message));
                    }
                    catch (ArgumentException exception)
                    {
                        schedule.LastError = exception.Message;
                    }
                    if (deliveries.Count >= maximumCount) return (deliveries, changed);
                }
            }
            return (deliveries, changed);
        });

    public Task RecordDeliveryResultAsync(ScheduledDelivery delivery, string? error) =>
        MutateAsync(data =>
        {
            if (!data.TryGetValue(delivery.GuildId.ToString(), out var guild)) return (false, false);
            // The ID deliberately prevents a late delivery result changing a newly
            // created schedule that reused a deleted schedule's name.
            if (delivery.IsReminder)
            {
                var schedule = guild.Reminders.FirstOrDefault(item => item.Id == delivery.ScheduleId);
                if (schedule is null) return (false, false);
                schedule.LastError = error;
            }
            else
            {
                var schedule = guild.AutoMessages.FirstOrDefault(item => item.Id == delivery.ScheduleId);
                if (schedule is null) return (false, false);
                schedule.LastError = error;
            }
            return (true, true);
        });

    internal static int SelectMessageIndex(AutoMessageSchedule schedule, Random random)
    {
        var count = schedule.Messages.Count;
        if (count == 0) throw new ArgumentException("The automessage has no messages.");
        int index;
        switch (schedule.Mode.ToLowerInvariant())
        {
            case "incremental":
                index = ((schedule.NextIndex % count) + count) % count;
                schedule.NextIndex = (index + 1) % count;
                break;
            case "random":
                index = random.Next(count);
                break;
            case "pseudorandom":
                // Ignore a malformed or stale bag rather than indexing an invalid message.
                if (schedule.ShuffleBag.Any(item => item < 0 || item >= count) ||
                    schedule.ShuffleBag.Count != schedule.ShuffleBag.Distinct().Count())
                    schedule.ShuffleBag.Clear();
                if (schedule.ShuffleBag.Count == 0)
                {
                    schedule.ShuffleBag = Enumerable.Range(0, count).ToList();
                    for (var i = count - 1; i > 0; i--)
                    {
                        var other = random.Next(i + 1);
                        (schedule.ShuffleBag[i], schedule.ShuffleBag[other]) =
                            (schedule.ShuffleBag[other], schedule.ShuffleBag[i]);
                    }
                    if (count > 1 && schedule.ShuffleBag[0] == schedule.LastIndex)
                    {
                        var other = random.Next(1, count);
                        (schedule.ShuffleBag[0], schedule.ShuffleBag[other]) =
                            (schedule.ShuffleBag[other], schedule.ShuffleBag[0]);
                    }
                }
                index = schedule.ShuffleBag[0];
                schedule.ShuffleBag.RemoveAt(0);
                break;
            default:
                throw new ArgumentException("Unknown automessage mode.");
        }
        schedule.LastIndex = index;
        return index;
    }

    private async Task<T> ReadAsync<T>(Func<Dictionary<string, ScheduledMessageGuildSettings>, T> read)
    {
        await _gate.WaitAsync();
        try { return read(await LoadUnsafeAsync()); }
        finally { _gate.Release(); }
    }

    private async Task<T> MutateAsync<T>(Func<Dictionary<string, ScheduledMessageGuildSettings>, (T Value, bool Changed)> update)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var (value, changed) = update(data);
            if (changed) await SaveUnsafeAsync(data);
            return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, ScheduledMessageGuildSettings>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        var json = await File.ReadAllTextAsync(_filePath);
        if (string.IsNullOrWhiteSpace(json)) return [];
        var data = JsonSerializer.Deserialize<Dictionary<string, ScheduledMessageGuildSettings>>(json, JsonOptions) ?? [];
        foreach (var key in data.Keys.ToList())
        {
            if (data[key] is not { } guild) { data.Remove(key); continue; }
            guild.AutoMessages = (guild.AutoMessages ?? []).OfType<AutoMessageSchedule>().ToList();
            guild.Reminders = (guild.Reminders ?? []).OfType<ReminderSchedule>().ToList();
            foreach (var schedule in guild.AutoMessages)
            {
                schedule.Messages ??= [];
                schedule.ShuffleBag ??= [];
            }
        }
        return data;
    }

    private async Task SaveUnsafeAsync(Dictionary<string, ScheduledMessageGuildSettings> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static ScheduledMessageGuildSettings GetGuild(Dictionary<string, ScheduledMessageGuildSettings> data, ulong guildId)
    {
        if (guildId == 0) throw new ArgumentException("A schedule must belong to a server.");
        if (!data.TryGetValue(guildId.ToString(), out var guild))
            data[guildId.ToString()] = guild = new ScheduledMessageGuildSettings();
        return guild;
    }

    private static bool SameName(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static AutoMessageSchedule? FindByName(IEnumerable<AutoMessageSchedule> schedules, string name) =>
        schedules.FirstOrDefault(schedule => SameName(schedule.Name, name));
    private static ReminderSchedule? FindByName(IEnumerable<ReminderSchedule> schedules, string name) =>
        schedules.FirstOrDefault(schedule => SameName(schedule.Name, name));

    private static void Validate(AutoMessageSchedule schedule)
    {
        ValidateCommon(schedule.Id, schedule.Name, schedule.Destination, schedule.Interval);
        if (!ScheduleRules.IsValidMode(schedule.Mode)) throw new ArgumentException("Unknown automessage mode.");
        schedule.Mode = schedule.Mode.ToLowerInvariant();
        if (schedule.Messages is null) throw new ArgumentException("The automessage needs a message list.");
        foreach (var message in schedule.Messages) ValidateMessage(message);
    }

    private static void Validate(ReminderSchedule schedule)
    {
        ValidateCommon(schedule.Id, schedule.Name, schedule.Destination, schedule.Delay);
        ValidateMessage(schedule.Message);
    }

    private static void ValidateCommon(string id, string name, ScheduledDestination destination, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A schedule needs an identity.");
        if (!ScheduleRules.IsValidName(name)) throw new ArgumentException("Names must be 1–100 letters, numbers, hyphens, or underscores.");
        if (destination is null || destination.TargetId == 0 || destination.Kind is not ("channel" or "user-dm"))
            throw new ArgumentException("The schedule needs a channel or member DM destination.");
        if (duration < ScheduleRules.MinimumTimespan || duration > ScheduleRules.MaximumTimespan)
            throw new ArgumentException("Timespans must be from 1 minute to 365 days.");
    }

    private static void ValidateMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > ScheduleRules.MaxMessageLength)
            throw new ArgumentException("Messages must contain 1–2000 characters.");
    }

    private static ScheduledDestination Clone(ScheduledDestination destination) => new()
    {
        Kind = destination?.Kind ?? string.Empty,
        TargetId = destination?.TargetId ?? 0
    };

    private static AutoMessageSchedule Clone(AutoMessageSchedule schedule) => new()
    {
        Id = schedule.Id,
        Name = schedule.Name,
        Destination = Clone(schedule.Destination),
        Interval = schedule.Interval,
        Mode = schedule.Mode,
        Messages = [.. schedule.Messages ?? []],
        Paused = schedule.Paused,
        NextDueAt = schedule.NextDueAt,
        NextIndex = schedule.NextIndex,
        ShuffleBag = [.. schedule.ShuffleBag ?? []],
        LastIndex = schedule.LastIndex,
        LastError = schedule.LastError
    };

    private static ReminderSchedule Clone(ReminderSchedule schedule) => new()
    {
        Id = schedule.Id,
        Name = schedule.Name,
        Destination = Clone(schedule.Destination),
        Delay = schedule.Delay,
        Message = schedule.Message,
        NextDueAt = schedule.NextDueAt,
        LastError = schedule.LastError
    };
}
