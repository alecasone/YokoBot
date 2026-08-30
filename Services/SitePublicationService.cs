using System.Collections.Concurrent;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class SitePublicationService
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(20);

    private readonly SiteSettingsStore _settings;
    private readonly PublicSiteExporter _exporter;
    private readonly GitHubContentsClient _github;
    private readonly TimeSpan _debounce;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _scheduled = new();
    private readonly CancellationTokenSource _stopping = new();

    public SitePublicationService(
        SiteSettingsStore settings,
        PublicSiteExporter exporter,
        GitHubContentsClient github,
        TimeSpan? debounce = null)
    {
        _settings = settings;
        _exporter = exporter;
        _github = github;
        _debounce = debounce ?? DefaultDebounce;
    }

    public bool HasToken => _github.HasToken;

    public async Task StartAsync()
    {
        foreach (var guildId in await _settings.GetPendingAutoPublishGuildIdsAsync())
            Schedule(guildId);
    }

    public async Task QueueAsync(ulong guildId)
    {
        await _settings.MarkPendingAsync(guildId);
        var settings = await _settings.GetAsync(guildId);
        if (settings.AutoPublish && settings.IsConfigured) Schedule(guildId);
    }

    public async Task SetAutoPublishAsync(ulong guildId, bool enabled)
    {
        await _settings.SetAutoPublishAsync(guildId, enabled);
        if (!enabled)
        {
            CancelScheduled(guildId);
            return;
        }

        var settings = await _settings.GetAsync(guildId);
        if (settings.IsConfigured) Schedule(guildId);
    }

    public async Task<SitePublishResult> PublishNowAsync(ulong guildId)
    {
        CancelScheduled(guildId);
        return await PublishInternalAsync(guildId);
    }

    public void Stop()
    {
        _stopping.Cancel();
        foreach (var scheduled in _scheduled.Values) scheduled.Cancel();
    }

    private void Schedule(ulong guildId)
    {
        if (_stopping.IsCancellationRequested) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        _scheduled.AddOrUpdate(guildId, cancellation, (_, previous) =>
        {
            previous.Cancel();
            return cancellation;
        });
        _ = PublishAfterDelayAsync(guildId, cancellation);
    }

    private async Task PublishAfterDelayAsync(ulong guildId, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_debounce, cancellation.Token);
            await PublishInternalAsync(guildId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await _settings.RecordFailureAsync(guildId, DateTimeOffset.UtcNow, exception.Message);
            Console.WriteLine($"Automatic Pages publish failed for server {guildId}: {exception}");
        }
        finally
        {
            if (_scheduled.TryGetValue(guildId, out var current) && ReferenceEquals(current, cancellation))
                _scheduled.TryRemove(guildId, out _);
            cancellation.Dispose();
        }
    }

    private async Task<SitePublishResult> PublishInternalAsync(ulong guildId)
    {
        try
        {
            await _publishGate.WaitAsync(_stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return new SitePublishResult(false, "The bot is shutting down.");
        }

        try
        {
            var settings = await _settings.GetAsync(guildId);
            if (!settings.IsConfigured)
                return await FailAsync(guildId, "Configure the Pages repository with `/siteadmin setup` first.");

            var json = await _exporter.BuildJsonAsync(guildId);
            var result = await _github.PublishAsync(settings, json);
            if (result.Success)
                await _settings.RecordSuccessAsync(guildId, DateTimeOffset.UtcNow, result.CommitSha, settings.ChangeVersion);
            else
                await _settings.RecordFailureAsync(guildId, DateTimeOffset.UtcNow, result.Message);
            return result;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return new SitePublishResult(false, "The bot is shutting down.");
        }
        catch (Exception exception)
        {
            return await FailAsync(guildId, $"Site export failed: {exception.Message}");
        }
        finally { _publishGate.Release(); }
    }

    private async Task<SitePublishResult> FailAsync(ulong guildId, string message)
    {
        await _settings.RecordFailureAsync(guildId, DateTimeOffset.UtcNow, message);
        return new SitePublishResult(false, message);
    }

    private void CancelScheduled(ulong guildId)
    {
        if (!_scheduled.TryRemove(guildId, out var scheduled)) return;
        scheduled.Cancel();
    }
}
