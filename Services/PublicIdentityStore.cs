using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class PublicIdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public PublicIdentityStore(string filePath) => _filePath = filePath;

    public async Task<PublicUserIdentity> GetOrCreateAsync(ulong guildId, ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var server = GetServer(data, guildId);
            if (!server.TryGetValue(userId.ToString(), out var identity))
            {
                server[userId.ToString()] = identity = NewIdentity();
                await SaveUnsafeAsync(data);
            }
            else if (identity.PublicId == Guid.Empty)
            {
                identity.PublicId = Guid.NewGuid();
                await SaveUnsafeAsync(data);
            }
            return Clone(identity);
        }
        finally { _gate.Release(); }
    }

    public async Task EnsureUsersAsync(ulong guildId, IEnumerable<ulong> userIds)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var server = GetServer(data, guildId);
            var changed = false;
            foreach (var userId in userIds.Distinct())
            {
                if (!server.TryGetValue(userId.ToString(), out var identity))
                {
                    server[userId.ToString()] = NewIdentity();
                    changed = true;
                }
                else if (identity.PublicId == Guid.Empty)
                {
                    identity.PublicId = Guid.NewGuid();
                    changed = true;
                }
            }
            if (changed) await SaveUnsafeAsync(data);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, Dictionary<string, PublicUserIdentity>>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, Dictionary<string, PublicUserIdentity>>>(stream, JsonOptions) ?? [];
    }

    private async Task SaveUnsafeAsync(Dictionary<string, Dictionary<string, PublicUserIdentity>> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static Dictionary<string, PublicUserIdentity> GetServer(
        Dictionary<string, Dictionary<string, PublicUserIdentity>> data,
        ulong guildId)
    {
        if (!data.TryGetValue(guildId.ToString(), out var server))
            data[guildId.ToString()] = server = [];
        return server;
    }

    private static PublicUserIdentity NewIdentity() => new() { PublicId = Guid.NewGuid() };

    private static PublicUserIdentity Clone(PublicUserIdentity identity) => new()
    {
        PublicId = identity.PublicId,
        Aliases = identity.Aliases.ToList(),
        CreatedAt = identity.CreatedAt
    };
}

