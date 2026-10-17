using System.Text.Json;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

internal static class ReminderCommandChecks
{
    internal static async Task RunAsync(string directory, DateTimeOffset now, Action<bool, string> check)
    {
        var path = Path.Combine(directory, "two-step-reminders.json");
        var store = new ScheduledMessageStore(path);
        check((await store.ActivateSelectedReminderAsync(1, now)).Reminder is null, "Unconfigured /remind created a timer.");
        check(await store.ConfigureReminderAsync(1, "bump", TimeSpan.FromHours(2), null, null) is null,
            "Incomplete setup created a reminder.");
        check((await store.GetRemindersAsync(1)).Count == 0, "Failed setup partially saved a reminder.");
        var saved = (await store.ConfigureReminderAsync(1, "bump", TimeSpan.FromHours(2),
            new() { TargetId = 888 }, "Time to bump!"))!;
        check(saved.NextDueAt is null && (await store.ClaimDueAsync(now.AddYears(1))).Count == 0,
            "Setup started the countdown instead of waiting for /remind.");
        store = new ScheduledMessageStore(path);
        check((await store.GetSelectedReminderAsync(1))?.Id == saved.Id, "Selected reminder did not survive restart.");
        check((await store.ActivateSelectedReminderAsync(2, now)).Reminder is null, "Reminder selection leaked across servers.");
        var activations = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.ActivateSelectedReminderAsync(1, now)));
        check(activations.Count(result => !result.AlreadyPending) == 1, "Concurrent /remind calls stacked timers.");
        check(activations.All(result => result.Reminder?.NextDueAt == now.AddHours(2)), "Concurrent activation changed the due time.");
        var repeat = await store.ActivateSelectedReminderAsync(1, now.AddMinutes(20));
        check(repeat.AlreadyPending && repeat.Reminder?.NextDueAt == now.AddHours(2), "Repeated /remind reset the countdown.");
        check((await store.ClaimDueAsync(now.AddHours(2))).Single().Message == "Time to bump!", "Saved reminder did not deliver its configured message.");
        check((await store.ClaimDueAsync(now.AddDays(1))).Count == 0, "One-shot reminder repeated without /remind.");
        check(!(await store.ActivateSelectedReminderAsync(1, now.AddDays(1))).AlreadyPending, "Completed reminder could not be restarted.");

        var edited = (await store.ConfigureReminderAsync(1, "BUMP", null, null, "Updated message"))!;
        check(edited.Id == saved.Id && edited.Delay == TimeSpan.FromHours(2) && edited.Destination.TargetId == 888,
            "Partial setup replaced the identity or cleared omitted settings.");
        check(edited.NextDueAt is null, "Editing settings left the old countdown active.");
        check((await store.ClaimDueAsync(now.AddYears(1))).Count == 0, "Edited settings sent before another /remind.");
        await store.ActivateSelectedReminderAsync(1, now.AddDays(2));
        check((await store.ClaimDueAsync(now.AddDays(2).AddHours(2))).Single().Message == "Updated message", "Activation ignored edited settings.");
        var other = (await store.ConfigureReminderAsync(1, "event", TimeSpan.FromMinutes(30), new() { TargetId = 999 }, "Event time"))!;
        check((await store.GetSelectedReminderAsync(1))?.Id == other.Id, "New setup did not select the new reminder.");
        check(await store.SelectReminderAsync(1, "bump"), "Could not select an existing reminder.");
        check((await store.GetSelectedReminderAsync(1))?.Id == saved.Id && (await store.GetReminderAsync(1, "bump"))?.NextDueAt is null,
            "Selecting a saved reminder started a timer or chose the wrong one.");
        check(!await store.SelectReminderAsync(1, "missing") && (await store.GetSelectedReminderAsync(1))?.Id == saved.Id,
            "Invalid selection cleared the saved selection.");
        await store.DeleteReminderAsync(1, "bump");
        store = new ScheduledMessageStore(path);
        check((await store.ActivateSelectedReminderAsync(1, now)).Reminder is null, "Deleting the selected reminder silently activated another one.");
        check((await store.GetRemindersAsync(1)).Single().Id == other.Id, "Deleting one reminder deleted another.");
        await store.SelectReminderAsync(1, "event");
        var eventActivation = await store.ActivateSelectedReminderAsync(1, now);
        check(eventActivation.Reminder?.Destination.TargetId == 999 && eventActivation.Reminder.Message == "Event time",
            "/remind did not use the selected template's destination and message.");

        // Existing saved reminders migrate without losing a pending countdown or guessing among several.
        var legacy = new ReminderSchedule { Name = "legacy", Delay = TimeSpan.FromHours(3),
            Destination = new() { TargetId = 888 }, Message = "Legacy message", NextDueAt = now.AddHours(3) };
        var legacyPath = Path.Combine(directory, "legacy-reminder-selection.json");
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["1"] = new { reminders = new[] { legacy } },
            ["2"] = new { reminders = new[] { legacy, other } }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var migrated = new ScheduledMessageStore(legacyPath);
        var legacyActivation = await migrated.ActivateSelectedReminderAsync(1, now);
        check(legacyActivation.AlreadyPending && legacyActivation.Reminder?.NextDueAt == now.AddHours(3),
            "Migration changed the legacy countdown.");
        check((await migrated.GetSelectedReminderAsync(1))?.Id == legacy.Id, "Sole legacy reminder was not selected.");
        check((await migrated.ActivateSelectedReminderAsync(2, now)).Reminder is null, "Migration guessed among multiple reminders.");
        await migrated.SelectReminderAsync(2, "event");
        check((await new ScheduledMessageStore(legacyPath).GetSelectedReminderAsync(2))?.Id == other.Id,
            "Explicit legacy selection did not persist.");
    }
}
