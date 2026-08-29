using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class AutoModerationRuleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public AutoModerationRuleStore(string filePath) => _filePath = filePath;

    public async Task<AutoModerationGuildRules> GetAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return GetGuild(data, guildId);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetTitlesAsync(ulong guildId)
    {
        var settings = await GetAsync(guildId);
        return settings.Rules.Keys.OrderBy(title => title).ToArray();
    }

    public async Task<string> AddRuleAsync(ulong guildId, AutoModerationRule rule)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetGuild(data, guildId);
            var title = Normalize(rule.Title);
            if (settings.Rules.ContainsKey(title)) return "exists";
            rule.Title = title;
            settings.Rules[title] = rule;
            await SaveUnsafeAsync(data);
            return "saved";
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteRuleAsync(ulong guildId, string title)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetGuild(data, guildId);
            var key = settings.Rules.Keys.FirstOrDefault(item =>
                item.Equals(Normalize(title), StringComparison.OrdinalIgnoreCase));
            if (key is null) return false;
            settings.Rules.Remove(key);
            settings.PendingActions.RemoveAll(item => item.RuleTitle.Equals(key, StringComparison.OrdinalIgnoreCase));
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task AddPendingAsync(ulong guildId, PendingModerationAction pending)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            GetGuild(data, guildId).PendingActions.Add(pending);
            await SaveUnsafeAsync(data);
        }
        finally { _gate.Release(); }
    }

    public async Task<PendingModerationAction?> GetPendingAsync(ulong guildId, ulong approvalMessageId)
    {
        var settings = await GetAsync(guildId);
        return settings.PendingActions.FirstOrDefault(item => item.ApprovalMessageId == approvalMessageId);
    }

    public async Task RemovePendingAsync(ulong guildId, ulong approvalMessageId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetGuild(data, guildId);
            settings.PendingActions.RemoveAll(item => item.ApprovalMessageId == approvalMessageId);
            await SaveUnsafeAsync(data);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, AutoModerationGuildRules>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, AutoModerationGuildRules>>(stream, JsonOptions) ?? [];
    }

    private async Task SaveUnsafeAsync(Dictionary<string, AutoModerationGuildRules> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static AutoModerationGuildRules GetGuild(
        Dictionary<string, AutoModerationGuildRules> data,
        ulong guildId)
    {
        if (!data.TryGetValue(guildId.ToString(), out var settings))
            data[guildId.ToString()] = settings = CreateDefaults();
        return settings;
    }

    private static AutoModerationGuildRules CreateDefaults()
    {
        var settings = new AutoModerationGuildRules();
        settings.Rules["unverified-2day-warn"] = new AutoModerationRule
        {
            Title = "unverified-2day-warn",
            Condition = "unverified",
            DelayHours = 48,
            MessageDestination = "dm-user",
            MessageTemplate = "Hey {user}, you've been unverified for two days. Please complete verification or you will be removed.",
            Action = "none"
        };
        settings.Rules["unverified-3day-kick"] = new AutoModerationRule
        {
            Title = "unverified-3day-kick",
            Condition = "unverified",
            DelayHours = 72,
            MessageDestination = "none",
            Action = "kick"
        };
        settings.Rules["inactive-30day-warn"] = new AutoModerationRule
        {
            Title = "inactive-30day-warn",
            Condition = "inactive",
            DelayHours = 720,
            MessageDestination = "dm-user",
            MessageTemplate = "Hey {user}, you haven't posted in 30 days. Please check in so staff knows you're still active.",
            Action = "none"
        };
        return settings;
    }

    internal static string Normalize(string title) =>
        string.Join('-', title.Trim().ToLowerInvariant().Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));
}
