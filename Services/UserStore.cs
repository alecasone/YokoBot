using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class UserStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public UserStore(string filePath) => _filePath = filePath;

    public async Task RegisterJoinAsync(ulong guildId, ulong userId, DateTimeOffset joinedAt)
    {
        await MutateAsync(data =>
        {
            var server = GetServer(data, guildId);
            server[userId.ToString()] = new ModeratedUser
            {
                JoinedAt = joinedAt,
                LastMessageAt = joinedAt,
                Verified = false
            };
        });
    }

    public async Task EnsureUserAsync(ulong guildId, ulong userId, DateTimeOffset joinedAt, bool verified)
    {
        await MutateAsync(data =>
        {
            var server = GetServer(data, guildId);
            if (!server.ContainsKey(userId.ToString()))
            {
                server[userId.ToString()] = new ModeratedUser
                {
                    JoinedAt = joinedAt,
                    LastMessageAt = joinedAt,
                    Verified = verified
                };
            }
        });
    }

    public async Task EnsureUsersAsync(
        ulong guildId,
        IEnumerable<(ulong UserId, DateTimeOffset JoinedAt, bool Verified)> users)
    {
        await MutateAsync(data =>
        {
            var server = GetServer(data, guildId);
            foreach (var (userId, joinedAt, verified) in users)
            {
                if (!server.ContainsKey(userId.ToString()))
                {
                    server[userId.ToString()] = new ModeratedUser
                    {
                        JoinedAt = joinedAt,
                        LastMessageAt = joinedAt,
                        Verified = verified
                    };
                }
            }
        });
    }

    public async Task RecordMessageAsync(ulong guildId, ulong userId, DateTimeOffset timestamp)
    {
        await MutateAsync(data =>
        {
            var server = GetServer(data, guildId);
            if (!server.TryGetValue(userId.ToString(), out var user))
            {
                server[userId.ToString()] = user = new ModeratedUser
                {
                    JoinedAt = timestamp,
                    Verified = false
                };
            }
            user.LastMessageAt = timestamp;
        });
    }

    public async Task MarkVerifiedAsync(ulong guildId, ulong userId, DateTimeOffset timestamp)
    {
        await MutateAsync(data =>
        {
            var server = GetServer(data, guildId);
            if (!server.TryGetValue(userId.ToString(), out var user))
            {
                server[userId.ToString()] = user = new ModeratedUser
                {
                    JoinedAt = timestamp,
                    LastMessageAt = timestamp
                };
            }
            user.Verified = true;
        });
    }

    public async Task<IReadOnlyList<(ulong UserId, ModeratedUser State)>> GetUsersAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!data.TryGetValue(guildId.ToString(), out var server)) return [];
            return server.Select(item => (ulong.Parse(item.Key), item.Value)).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveUserAsync(ulong guildId, ulong userId)
    {
        await MutateAsync(data =>
        {
            if (!data.TryGetValue(guildId.ToString(), out var server)) return;
            server.Remove(userId.ToString());
            if (server.Count == 0) data.Remove(guildId.ToString());
        });
    }

    public async Task MarkRuleExecutedAsync(ulong guildId, ulong userId, string ruleTitle, DateTimeOffset timestamp)
    {
        await MutateAsync(data =>
        {
            if (!data.TryGetValue(guildId.ToString(), out var server) ||
                !server.TryGetValue(userId.ToString(), out var user)) return;
            user.RuleExecutions[ruleTitle] = timestamp;
        });
    }

    public async Task<(int Promoted, int Demoted)> ReconcileVerificationStatesAsync(
        ulong guildId,
        IEnumerable<ulong> verifiedUserIds,
        IEnumerable<ulong> unverifiedUserIds,
        DateTimeOffset timestamp)
    {
        var promoted = 0;
        var demoted = 0;
        var verifiedIds = verifiedUserIds.Select(id => id.ToString()).ToHashSet();
        var unverifiedIds = unverifiedUserIds.Select(id => id.ToString()).ToHashSet();
        await MutateAsync(data =>
        {
            if (!data.TryGetValue(guildId.ToString(), out var server)) return;
            foreach (var userId in verifiedIds)
            {
                if (server.TryGetValue(userId, out var user) && !user.Verified)
                {
                    user.Verified = true;
                    promoted++;
                }
            }
            foreach (var userId in unverifiedIds)
            {
                if (server.TryGetValue(userId, out var user) && user.Verified)
                {
                    user.Verified = false;
                    user.JoinedAt = timestamp;
                    user.RuleExecutions.Clear();
                    demoted++;
                }
            }
        });
        return (promoted, demoted);
    }

    private async Task MutateAsync(Action<Dictionary<string, Dictionary<string, ModeratedUser>>> mutation)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            mutation(data);
            await SaveUnsafeAsync(data);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, Dictionary<string, ModeratedUser>>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, Dictionary<string, ModeratedUser>>>(stream, JsonOptions) ?? [];
    }

    private async Task SaveUnsafeAsync(Dictionary<string, Dictionary<string, ModeratedUser>> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static Dictionary<string, ModeratedUser> GetServer(
        Dictionary<string, Dictionary<string, ModeratedUser>> data,
        ulong guildId)
    {
        if (!data.TryGetValue(guildId.ToString(), out var server))
            data[guildId.ToString()] = server = [];
        return server;
    }
}
