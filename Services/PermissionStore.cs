using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class PermissionStore
{
    private const int CurrentSeedVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public PermissionStore(string filePath) => _filePath = filePath;

    public async Task<IReadOnlyDictionary<string, PermissionGrant>> GetGrantsAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var (data, settings, changed) = await LoadGuildUnsafeAsync(guildId);
            if (changed) await SaveUnsafeAsync(data);
            return settings.Grants.ToDictionary(
                pair => pair.Key,
                pair => Clone(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }
        finally { _gate.Release(); }
    }

    public Task<bool> GrantRoleAsync(ulong guildId, string permission, ulong roleId) =>
        MutateAsync(guildId, permission, grant => AddUnique(grant.RoleIds, roleId));

    public Task<bool> RevokeRoleAsync(ulong guildId, string permission, ulong roleId) =>
        MutateAsync(guildId, permission, grant => grant.RoleIds.Remove(roleId));

    public Task<bool> GrantUserAsync(ulong guildId, string permission, ulong userId) =>
        MutateAsync(guildId, permission, grant => AddUnique(grant.UserIds, userId));

    public Task<bool> RevokeUserAsync(ulong guildId, string permission, ulong userId) =>
        MutateAsync(guildId, permission, grant => grant.UserIds.Remove(userId));

    private async Task<bool> MutateAsync(
        ulong guildId,
        string permission,
        Func<PermissionGrant, bool> mutation)
    {
        var normalized = PermissionCatalog.Normalize(permission);
        await _gate.WaitAsync();
        try
        {
            var (data, settings, seeded) = await LoadGuildUnsafeAsync(guildId);
            if (!settings.Grants.TryGetValue(normalized, out var grant))
                settings.Grants[normalized] = grant = new PermissionGrant();
            var changed = mutation(grant);
            if (grant.RoleIds.Count == 0 && grant.UserIds.Count == 0)
                settings.Grants.Remove(normalized);
            if (changed || seeded) await SaveUnsafeAsync(data);
            return changed;
        }
        finally { _gate.Release(); }
    }

    private async Task<(PermissionData Data, GuildPermissionSettings Settings, bool Changed)> LoadGuildUnsafeAsync(ulong guildId)
    {
        var data = await LoadUnsafeAsync();
        var key = guildId.ToString();
        if (!data.Guilds.TryGetValue(key, out var settings))
        {
            settings = new GuildPermissionSettings
            {
                SeedVersion = CurrentSeedVersion,
                Grants = PermissionCatalog.CreateSeedGrants().ToDictionary(
                    pair => pair.Key,
                    pair => Clone(pair.Value),
                    StringComparer.OrdinalIgnoreCase)
            };
            data.Guilds[key] = settings;
            return (data, settings, true);
        }

        settings.Grants = new Dictionary<string, PermissionGrant>(settings.Grants, StringComparer.OrdinalIgnoreCase);
        if (settings.SeedVersion >= CurrentSeedVersion) return (data, settings, false);

        if (settings.SeedVersion < 1)
        {
            foreach (var (permission, seedGrant) in PermissionCatalog.CreateSeedGrants())
            {
                if (!settings.Grants.TryGetValue(permission, out var grant))
                    settings.Grants[permission] = grant = new PermissionGrant();
                foreach (var roleId in seedGrant.RoleIds) AddUnique(grant.RoleIds, roleId);
                foreach (var userId in seedGrant.UserIds) AddUnique(grant.UserIds, userId);
            }
        }
        if (settings.SeedVersion < 2)
        {
            foreach (var permission in new[]
                     {
                         "relationship.request", "relationship.respond", "relationship.remove", "relationship.view"
                     })
            {
                if (!settings.Grants.TryGetValue(permission, out var grant))
                    settings.Grants[permission] = grant = new PermissionGrant();
                AddUnique(grant.RoleIds, PermissionCatalog.VerifiedRoleId);
                AddUnique(grant.RoleIds, PermissionCatalog.ModeratorRoleId);
            }
        }
        if (settings.SeedVersion < 3)
        {
            if (!settings.Grants.TryGetValue("alerts.view", out var grant))
                settings.Grants["alerts.view"] = grant = new PermissionGrant();
            AddUnique(grant.RoleIds, PermissionCatalog.ModeratorRoleId);
        }
        settings.SeedVersion = CurrentSeedVersion;
        return (data, settings, true);
    }

    private async Task<PermissionData> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return new PermissionData();
        var json = await File.ReadAllTextAsync(_filePath);
        if (string.IsNullOrWhiteSpace(json)) return new PermissionData();
        return JsonSerializer.Deserialize<PermissionData>(json, JsonOptions) ?? new PermissionData();
    }

    private async Task SaveUnsafeAsync(PermissionData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static PermissionGrant Clone(PermissionGrant grant) => new()
    {
        RoleIds = grant.RoleIds.ToList(),
        UserIds = grant.UserIds.ToList()
    };

    private static bool AddUnique(List<ulong> values, ulong value)
    {
        if (values.Contains(value)) return false;
        values.Add(value);
        return true;
    }
}
