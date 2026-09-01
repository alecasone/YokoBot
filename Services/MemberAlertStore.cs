using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class MemberAlertStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public MemberAlertStore(string filePath) => _filePath = filePath;

    public async Task<MemberAlertGuildSettings> GetAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return data.TryGetValue(guildId.ToString(), out var settings)
                ? Clone(settings)
                : new MemberAlertGuildSettings();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> AddLeaveAlertAsync(ulong guildId, RoleLeaveAlert alert)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetGuild(data, guildId);
            var key = alert.RoleId.ToString();
            if (settings.LeaveAlerts.ContainsKey(key)) return false;
            settings.LeaveAlerts[key] = Clone(alert);
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ReplaceLeaveAlertAsync(ulong guildId, RoleLeaveAlert alert)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!data.TryGetValue(guildId.ToString(), out var settings)) return false;
            var key = alert.RoleId.ToString();
            if (!settings.LeaveAlerts.ContainsKey(key)) return false;
            settings.LeaveAlerts[key] = Clone(alert);
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteLeaveAlertAsync(ulong guildId, ulong roleId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!data.TryGetValue(guildId.ToString(), out var settings) ||
                !settings.LeaveAlerts.Remove(roleId.ToString()))
                return false;
            RemoveEmptyGuild(data, guildId, settings);
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task SetNewAccountAlertAsync(ulong guildId, NewAccountAlert alert)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            GetGuild(data, guildId).NewAccountAlert = Clone(alert);
            await SaveUnsafeAsync(data);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteNewAccountAlertAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!data.TryGetValue(guildId.ToString(), out var settings) || settings.NewAccountAlert is null)
                return false;
            settings.NewAccountAlert = null;
            RemoveEmptyGuild(data, guildId, settings);
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, MemberAlertGuildSettings>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        var json = await File.ReadAllTextAsync(_filePath);
        if (string.IsNullOrWhiteSpace(json)) return [];
        var data = JsonSerializer.Deserialize<Dictionary<string, MemberAlertGuildSettings>>(json, JsonOptions) ?? [];
        foreach (var settings in data.Values)
            settings.LeaveAlerts = new Dictionary<string, RoleLeaveAlert>(
                settings.LeaveAlerts ?? [], StringComparer.OrdinalIgnoreCase);
        return data;
    }

    private async Task SaveUnsafeAsync(Dictionary<string, MemberAlertGuildSettings> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static MemberAlertGuildSettings GetGuild(
        IDictionary<string, MemberAlertGuildSettings> data,
        ulong guildId)
    {
        if (!data.TryGetValue(guildId.ToString(), out var settings))
            data[guildId.ToString()] = settings = new MemberAlertGuildSettings();
        return settings;
    }

    private static void RemoveEmptyGuild(
        IDictionary<string, MemberAlertGuildSettings> data,
        ulong guildId,
        MemberAlertGuildSettings settings)
    {
        if (settings.LeaveAlerts.Count == 0 && settings.NewAccountAlert is null)
            data.Remove(guildId.ToString());
    }

    private static MemberAlertGuildSettings Clone(MemberAlertGuildSettings settings) => new()
    {
        LeaveAlerts = settings.LeaveAlerts.ToDictionary(
            pair => pair.Key,
            pair => Clone(pair.Value),
            StringComparer.OrdinalIgnoreCase),
        NewAccountAlert = settings.NewAccountAlert is null ? null : Clone(settings.NewAccountAlert)
    };

    private static RoleLeaveAlert Clone(RoleLeaveAlert alert) => new()
    {
        RoleId = alert.RoleId,
        Destination = Clone(alert.Destination),
        MessageTemplate = alert.MessageTemplate
    };

    private static NewAccountAlert Clone(NewAccountAlert alert) => new()
    {
        MaximumAccountAgeDays = alert.MaximumAccountAgeDays,
        Destination = Clone(alert.Destination),
        MessageTemplate = alert.MessageTemplate
    };

    private static AlertDestination Clone(AlertDestination destination) => new()
    {
        Kind = destination.Kind,
        TargetId = destination.TargetId
    };
}
