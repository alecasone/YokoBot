using System.Text.Json;
using Discord;
using Yoko.Bot.Commands;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

internal static class ScheduledMessageChecks
{
    internal static async Task RunAsync(string directory, Action<bool, string> check)
    {
        check(AutoMessageCommands.Build() is not null, "Automessage slash command failed Discord validation.");
        var reminderCommand = (SlashCommandProperties)ReminderCommands.Build();
        var remindCommand = (SlashCommandProperties)ReminderCommands.BuildRemind();
        check(remindCommand.Name.Value == "remind" && (!remindCommand.Options.IsSpecified || !remindCommand.Options.Value.Any()),
            "/remind must have no fields, including optional fields.");
        check(reminderCommand.Options.Value.Single(option => option.Name == "name").IsRequired == true &&
              reminderCommand.Options.Value.Single(option => option.Name == "action").Choices.All(choice => choice.Name != "activate"),
            "/reminder must manage templates without activating them.");
        check(!ReminderCommands.EphemeralResponses, "Reminder responses must be public, including authorization errors.");

        check(ScheduleRules.TryParseTimespan("1h30m", out var duration) && duration == TimeSpan.FromMinutes(90),
            "Combined schedule timespan was parsed incorrectly.");
        foreach (var invalid in new[] { "0m", "30s", "-2h", "366d", "999999999999999999999w", "2h garbage", "NaNh", "" })
            check(!ScheduleRules.TryParseTimespan(invalid, out _), $"Invalid schedule timespan accepted: {invalid}");

        var selection = new AutoMessageSchedule { Mode = "incremental", Messages = ["A", "B", "C"] };
        var random = new Random(71);
        check(Enumerable.Range(0, 8).Select(_ => ScheduledMessageStore.SelectMessageIndex(selection, random))
                .SequenceEqual([0, 1, 2, 0, 1, 2, 0, 1]), "Incremental mode did not wrap in list order.");
        selection = new AutoMessageSchedule { Mode = "pseudorandom", Messages = ["A", "B", "C"] };
        int? previous = null;
        for (var cycle = 0; cycle < 12; cycle++)
        {
            var picks = Enumerable.Range(0, 3).Select(_ => ScheduledMessageStore.SelectMessageIndex(selection, random)).ToArray();
            check(picks.Distinct().Count() == 3, "Pseudorandom repeated before exhausting the list.");
            check(previous is null || previous != picks[0], "Pseudorandom repeated at a cycle boundary.");
            previous = picks[^1];
        }
        selection = new AutoMessageSchedule { Mode = "pseudorandom", Messages = ["Only one"] };
        check(Enumerable.Range(0, 4).All(_ => ScheduledMessageStore.SelectMessageIndex(selection, random) == 0),
            "Single-message pseudorandom schedule failed.");
        selection = new AutoMessageSchedule { Mode = "random", Messages = ["A", "B", "C"] };
        var independentRandom = new Random(71);
        random = new Random(71);
        check(Enumerable.Range(0, 20).All(_ => ScheduledMessageStore.SelectMessageIndex(selection, random) == independentRandom.Next(3)),
            "Random mode is not an independent selection on each tick.");

        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(directory, "scheduled-messages.json");
        var store = new ScheduledMessageStore(path);
        var automatic = new AutoMessageSchedule
        {
            Name = "Greetings", Destination = new() { TargetId = 777 }, Interval = TimeSpan.FromHours(2),
            Mode = "pseudorandom", Messages = ["A", "B", "C"], NextDueAt = now
        };
        check(await store.CreateAutoMessageAsync(1, automatic), "Automessage creation failed.");
        check(!await store.CreateAutoMessageAsync(1, new()
        {
            Name = "gREETINGS", Destination = new() { TargetId = 777 }, Interval = TimeSpan.FromHours(2)
        }), "Case-insensitive duplicate schedule name was allowed.");
        var returned = (await store.GetAutoMessageAsync(1, "greetings"))!;
        returned.Messages.Clear();
        check((await store.GetAutoMessageAsync(1, "Greetings"))!.Messages.Count == 3, "Store exposed mutable state.");
        check(await store.GetAutoMessageAsync(2, "Greetings") is null, "Automessages leaked across guilds.");

        var first = (await store.ClaimDueAsync(now, new Random(2))).Single();
        var reopened = new ScheduledMessageStore(path);
        check((await reopened.GetAutoMessageAsync(1, "Greetings"))!.ShuffleBag.Count == 2, "Shuffle progress did not persist.");
        check((await reopened.ClaimDueAsync(now.AddHours(1))).Count == 0, "Restart replayed an already claimed tick.");
        var second = (await reopened.ClaimDueAsync(now.AddHours(2), new Random(91))).Single();
        var third = (await reopened.ClaimDueAsync(now.AddHours(4), new Random(92))).Single();
        check(new[] { first.Message, second.Message, third.Message }.Distinct().Count() == 3,
            "Restart lost the current pseudorandom cycle.");
        var storedAutomatic = (await reopened.GetAutoMessageAsync(1, "Greetings"))!;
        await reopened.UpdateAutoMessageAsync(1, storedAutomatic.Id, schedule => schedule.Paused = true);
        check((await reopened.ClaimDueAsync(now.AddDays(1))).Count == 0, "Paused automessage was delivered.");
        await reopened.UpdateAutoMessageAsync(1, storedAutomatic.Id, schedule => schedule.Paused = false);
        check((await reopened.ClaimDueAsync(now.AddDays(1))).Count == 1 &&
              (await reopened.ClaimDueAsync(now.AddDays(1))).Count == 0,
            "An overdue schedule generated a backlog burst.");
        await reopened.DeleteAutoMessageAsync(1, "Greetings");
        check(await reopened.CreateAutoMessageAsync(1, new()
        {
            Name = "Greetings", Destination = new() { TargetId = 777 }, Interval = TimeSpan.FromHours(2)
        }), "Deleted name could not be reused.");
        check(!await reopened.UpdateAutoMessageAsync(1, storedAutomatic.Id, schedule => schedule.Messages.Add("Stale")),
            "Stale editor changed a replacement schedule.");

        var reminder = new ReminderSchedule
        {
            Name = "bump", Destination = new() { TargetId = 888 }, Delay = TimeSpan.FromHours(2),
            Message = "Time to bump!", NextDueAt = now.AddHours(2)
        };
        check(await reopened.CreateReminderAsync(1, reminder), "Reminder creation failed.");
        var deliveries = new List<string>();
        var scheduler = new ScheduledMessageService(reopened, (guild, destination, message) =>
        {
            check(guild == 1 && destination.TargetId == 888, "Reminder delivery changed its destination.");
            deliveries.Add(message);
            return Task.CompletedTask;
        }, () => now);
        await scheduler.ProcessDueAsync(now.AddHours(1));
        check(deliveries.Count == 0, "Reminder fired early.");
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => scheduler.ProcessDueAsync(now.AddHours(2))));
        check(deliveries.SequenceEqual(["Time to bump!"]), "Concurrent scheduler ticks delivered a reminder multiple times.");
        var fired = (await reopened.GetReminderAsync(1, "BUMP"))!;
        check(fired.NextDueAt is null, "Reminder was not disarmed after firing.");
        await scheduler.ProcessDueAsync(now.AddDays(3));
        check(deliveries.Count == 1, "One-shot reminder automatically repeated.");
        await reopened.UpdateReminderAsync(1, fired.Id, item => item.NextDueAt = now.AddDays(4));
        await new ScheduledMessageService(new ScheduledMessageStore(path), (_, _, message) =>
        {
            deliveries.Add(message);
            return Task.CompletedTask;
        }).ProcessDueAsync(now.AddDays(4));
        check(deliveries.Count == 2, "Rearmed reminder was lost across restart.");

        await reopened.UpdateReminderAsync(1, fired.Id, item => item.NextDueAt = now.AddDays(5));
        var failureScheduler = new ScheduledMessageService(reopened,
            (_, _, _) => Task.FromException(new InvalidOperationException("DMs disabled")));
        await failureScheduler.ProcessDueAsync(now.AddDays(5));
        var failed = (await reopened.GetReminderAsync(1, "bump"))!;
        check(failed.NextDueAt is null && failed.LastError?.Contains("DMs disabled") == true,
            "Delivery failure was not recorded or left a reminder armed.");
        await failureScheduler.ProcessDueAsync(now.AddDays(6));
        check(deliveries.Count == 2, "Failed reminder was automatically retried.");

        await ReminderCommandChecks.RunAsync(directory, now, check);

        // A slow Discord send must not preclaim unrelated schedules and defeat cancellation.
        var raceStore = new ScheduledMessageStore(Path.Combine(directory, "schedule-dispatch-races.json"));
        await raceStore.CreateAutoMessageAsync(1, new()
        {
            Name = "blocker", Destination = new() { TargetId = 777 }, Interval = TimeSpan.FromHours(2),
            Messages = ["blocking send"], NextDueAt = now
        });
        var pauseTarget = new AutoMessageSchedule
        {
            Name = "pause-me", Destination = new() { TargetId = 777 }, Interval = TimeSpan.FromHours(2),
            Messages = ["paused message"], NextDueAt = now
        };
        await raceStore.CreateAutoMessageAsync(1, pauseTarget);
        var cancelTarget = new ReminderSchedule
        {
            Name = "cancel-me", Destination = new() { TargetId = 777 }, Delay = TimeSpan.FromHours(2),
            Message = "cancelled message", NextDueAt = now
        };
        await raceStore.CreateReminderAsync(1, cancelTarget);
        await raceStore.CreateReminderAsync(1, new()
        {
            Name = "delete-me", Destination = new() { TargetId = 777 }, Delay = TimeSpan.FromHours(2),
            Message = "deleted message", NextDueAt = now
        });
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raceDeliveries = new List<string>();
        var raceScheduler = new ScheduledMessageService(raceStore, async (_, _, message) =>
        {
            raceDeliveries.Add(message);
            sendStarted.TrySetResult();
            await releaseSend.Task;
        });
        var dispatch = raceScheduler.ProcessDueAsync(now);
        try
        {
            await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check((await raceStore.GetReminderAsync(1, "cancel-me"))!.NextDueAt == now,
                "An unrelated reminder was disarmed before its send began.");
            await raceStore.UpdateAutoMessageAsync(1, pauseTarget.Id, item => { item.Paused = true; item.NextDueAt = null; });
            await raceStore.UpdateReminderAsync(1, cancelTarget.Id, item => item.NextDueAt = null);
            await raceStore.DeleteReminderAsync(1, "delete-me");
        }
        finally { releaseSend.TrySetResult(); }
        await dispatch;
        check(raceDeliveries.SequenceEqual(["blocking send"]),
            "Pausing, cancelling, or deleting an awaiting schedule did not stop its delivery.");

        var permissionPath = Path.Combine(directory, "schedule-permissions.json");
        await File.WriteAllTextAsync(permissionPath, JsonSerializer.Serialize(new PermissionData
        {
            Guilds = new() { ["1"] = new() { SeedVersion = 3 } }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var permissions = new PermissionStore(permissionPath);
        var migrated = await permissions.GetGrantsAsync(1);
        check(migrated["reminder.activate"].RoleIds.Contains(PermissionCatalog.VerifiedRoleId) &&
              migrated["reminder.activate"].RoleIds.Contains(PermissionCatalog.ModeratorRoleId),
            "Existing server roles did not receive reminder activation.");
        await permissions.RevokeRoleAsync(1, "reminder.activate", PermissionCatalog.VerifiedRoleId);
        check(!(await new PermissionStore(permissionPath).GetGrantsAsync(1))["reminder.activate"].RoleIds
            .Contains(PermissionCatalog.VerifiedRoleId), "Restart restored a revoked reminder permission.");
        check(PermissionCatalog.IsKnownOrWildcard("automessage.manage") && PermissionCatalog.IsKnownOrWildcard("reminder.*"),
            "New command permissions cannot be assigned.");
    }
}
