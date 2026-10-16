using Discord;
using Discord.WebSocket;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class ScheduledMessageService
{
    private readonly ScheduledMessageStore _store;
    private readonly Func<ulong, ScheduledDestination, string, Task> _sendAsync;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Random _random;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;

    public ScheduledMessageService(DiscordSocketClient client, ScheduledMessageStore store)
        : this(store, (guildId, destination, message) => SendDiscordAsync(client, guildId, destination, message))
    {
    }

    // The sender and clock can be replaced in checks without connecting to Discord.
    public ScheduledMessageService(
        ScheduledMessageStore store,
        Func<ulong, ScheduledDestination, string, Task> sendAsync,
        Func<DateTimeOffset>? utcNow = null,
        Random? random = null)
    {
        _store = store;
        _sendAsync = sendAsync;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _random = random ?? Random.Shared;
    }

    public Task StartAsync()
    {
        lock (_lifecycleGate)
        {
            if (_loopTask is { IsCompleted: false }) return Task.CompletedTask;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _loopTask = RunAsync(_cancellation.Token);
        }
        return Task.CompletedTask;
    }

    public void Stop()
    {
        lock (_lifecycleGate) _cancellation?.Cancel();
    }

    public async Task ProcessDueAsync(DateTimeOffset? now = null)
    {
        await _dispatchGate.WaitAsync();
        try
        {
            var dueAt = now ?? _utcNow();
            while (_cancellation?.IsCancellationRequested != true)
            {
                // Claim only the next send. Other due records stay cancellable while
                // this delivery is waiting on Discord; a send already in flight cannot
                // be recalled. A fixed cutoff prevents another tick in this pass.
                var deliveries = await _store.ClaimDueAsync(dueAt, _random, maximumCount: 1);
                if (deliveries.Count == 0) break;
                var delivery = deliveries[0];
                string? error = null;
                try
                {
                    await _sendAsync(delivery.GuildId, delivery.Destination, delivery.Message);
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    Console.WriteLine($"Could not deliver scheduled message '{delivery.Name}' in {delivery.GuildId}: {error}");
                }
                try
                {
                    await _store.RecordDeliveryResultAsync(delivery, error);
                }
                catch (Exception exception)
                {
                    // The due time was already committed; a result logging failure
                    // must not prevent another schedule from sending.
                    Console.WriteLine($"Could not save scheduled message result '{delivery.Name}' in {delivery.GuildId}: {exception.Message}");
                }
            }
        }
        finally { _dispatchGate.Release(); }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Scheduled message processing failed: {exception.Message}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static async Task SendDiscordAsync(
        DiscordSocketClient client,
        ulong guildId,
        ScheduledDestination destination,
        string message)
    {
        var guild = client.GetGuild(guildId)
                    ?? throw new InvalidOperationException("The schedule's server is unavailable.");
        switch (destination.Kind)
        {
            case "channel":
            {
                var channel = guild.GetChannel(destination.TargetId) as IMessageChannel ??
                              guild.GetThreadChannel(destination.TargetId) as IMessageChannel;
                if (channel is null)
                {
                    var fetched = await client.Rest.GetChannelAsync(destination.TargetId);
                    if (fetched is IGuildChannel guildChannel && guildChannel.GuildId == guildId)
                        channel = fetched as IMessageChannel;
                }
                if (channel is null)
                    throw new InvalidOperationException("The destination is not an available message channel in this server.");
                await channel.SendMessageAsync(message,
                    options: new RequestOptions { RetryMode = RetryMode.RetryRatelimit });
                break;
            }
            case "user-dm":
            {
                var member = guild.GetUser(destination.TargetId) as IGuildUser ??
                             await ((IGuild)guild).GetUserAsync(destination.TargetId);
                if (member is null)
                    throw new InvalidOperationException("The DM recipient is no longer a member of this server.");
                await member.SendMessageAsync(message,
                    options: new RequestOptions { RetryMode = RetryMode.RetryRatelimit });
                break;
            }
            default:
                throw new InvalidOperationException("Unknown scheduled message destination.");
        }
    }
}
