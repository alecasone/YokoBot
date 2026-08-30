using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class SiteSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public SiteSettingsStore(string filePath) => _filePath = filePath;

    public async Task<SiteGuildSettings> GetAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return data.TryGetValue(guildId.ToString(), out var settings)
                ? Clone(settings)
                : new SiteGuildSettings();
        }
        finally { _gate.Release(); }
    }

    public Task ConfigureAsync(
        ulong guildId,
        string owner,
        string repository,
        string branch,
        string dataPath,
        string baseUrl) => MutateAsync(guildId, settings =>
    {
        settings.RepositoryOwner = owner;
        settings.RepositoryName = repository;
        settings.Branch = branch;
        settings.DataPath = dataPath;
        settings.BaseUrl = baseUrl;
        settings.PendingChanges = true;
        settings.ChangeVersion++;
        settings.LastError = null;
    });

    public Task SetAutoPublishAsync(ulong guildId, bool enabled) => MutateAsync(guildId, settings =>
    {
        settings.AutoPublish = enabled;
        if (enabled)
        {
            settings.PendingChanges = true;
            settings.ChangeVersion++;
        }
    });

    public Task MarkPendingAsync(ulong guildId) => MutateAsync(guildId, settings =>
    {
        settings.PendingChanges = true;
        settings.ChangeVersion++;
    });

    public Task RecordSuccessAsync(ulong guildId, DateTimeOffset timestamp, string? commitSha, long publishedVersion) =>
        MutateAsync(guildId, settings =>
        {
            settings.LastAttemptAt = timestamp;
            settings.LastPublishedAt = timestamp;
            settings.LastCommitSha = commitSha;
            settings.LastError = null;
            settings.LastPublishedVersion = Math.Max(settings.LastPublishedVersion, publishedVersion);
            settings.PendingChanges = settings.ChangeVersion > settings.LastPublishedVersion;
        });

    public Task RecordFailureAsync(ulong guildId, DateTimeOffset timestamp, string error) =>
        MutateAsync(guildId, settings =>
        {
            settings.LastAttemptAt = timestamp;
            settings.LastError = error;
            settings.PendingChanges = true;
        });

    public async Task<IReadOnlyList<ulong>> GetPendingAutoPublishGuildIdsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return data
                .Where(pair => pair.Value.AutoPublish && pair.Value.PendingChanges && pair.Value.IsConfigured)
                .Select(pair => ulong.TryParse(pair.Key, out var guildId) ? guildId : 0)
                .Where(guildId => guildId != 0)
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    private async Task MutateAsync(ulong guildId, Action<SiteGuildSettings> mutation)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!data.TryGetValue(guildId.ToString(), out var settings))
                data[guildId.ToString()] = settings = new SiteGuildSettings();
            mutation(settings);
            await SaveUnsafeAsync(data);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, SiteGuildSettings>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, SiteGuildSettings>>(stream, JsonOptions) ?? [];
    }

    private async Task SaveUnsafeAsync(Dictionary<string, SiteGuildSettings> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static SiteGuildSettings Clone(SiteGuildSettings settings) => new()
    {
        RepositoryOwner = settings.RepositoryOwner,
        RepositoryName = settings.RepositoryName,
        Branch = settings.Branch,
        DataPath = settings.DataPath,
        BaseUrl = settings.BaseUrl,
        AutoPublish = settings.AutoPublish,
        PendingChanges = settings.PendingChanges,
        ChangeVersion = settings.ChangeVersion,
        LastPublishedVersion = settings.LastPublishedVersion,
        LastAttemptAt = settings.LastAttemptAt,
        LastPublishedAt = settings.LastPublishedAt,
        LastCommitSha = settings.LastCommitSha,
        LastError = settings.LastError
    };
}
