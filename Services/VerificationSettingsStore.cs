using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class VerificationSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public VerificationSettingsStore(string filePath) => _filePath = filePath;

    public async Task<VerificationGuildSettings> GetAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return GetSettings(data, guildId);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetTypesAsync(ulong guildId)
    {
        var settings = await GetAsync(guildId);
        return settings.Profiles.Keys.OrderBy(name => name).ToArray();
    }

    public async Task SetSuccessMessageAsync(ulong guildId, ulong channelId, string message)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetSettings(data, guildId);
            settings.SuccessChannelId = channelId;
            settings.SuccessMessage = message;
            await SaveUnsafeAsync(data);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> CreateAsync(ulong guildId, string type)
    {
        var normalized = Normalize(type);
        if (string.IsNullOrWhiteSpace(normalized)) return "invalid";
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetSettings(data, guildId);
            if (settings.Profiles.ContainsKey(normalized)) return "exists";
            settings.Profiles[normalized] = new VerificationProfile();
            await SaveUnsafeAsync(data);
            return "saved";
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ReplaceAsync(
        ulong guildId,
        string type,
        IEnumerable<ulong> addedRoleIds,
        IEnumerable<ulong> removedRoleIds)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetSettings(data, guildId);
            var key = settings.Profiles.Keys.FirstOrDefault(item =>
                item.Equals(Normalize(type), StringComparison.OrdinalIgnoreCase));
            if (key is null) return false;

            var removed = removedRoleIds.Distinct().ToArray();
            settings.Profiles[key] = new VerificationProfile
            {
                RemovedRoleIds = [.. removed],
                AddedRoleIds = [.. addedRoleIds.Distinct().Except(removed)]
            };
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(ulong guildId, string type)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetSettings(data, guildId);
            var key = settings.Profiles.Keys.FirstOrDefault(item =>
                item.Equals(Normalize(type), StringComparison.OrdinalIgnoreCase));
            if (key is null) return false;
            settings.Profiles.Remove(key);
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, VerificationGuildSettings>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, VerificationGuildSettings>>(stream, JsonOptions) ?? [];
    }

    private async Task SaveUnsafeAsync(Dictionary<string, VerificationGuildSettings> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static VerificationGuildSettings GetSettings(
        Dictionary<string, VerificationGuildSettings> data,
        ulong guildId)
    {
        if (!data.TryGetValue(guildId.ToString(), out var settings))
            data[guildId.ToString()] = settings = new VerificationGuildSettings();
        return settings;
    }

    internal static string Normalize(string type) =>
        string.Join('-', type.Trim().ToLowerInvariant().Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));
}
