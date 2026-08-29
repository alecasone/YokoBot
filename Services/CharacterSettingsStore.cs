using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class CharacterSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public CharacterSettingsStore(string filePath) => _filePath = filePath;

    public async Task<IReadOnlyList<string>> GetDefaultPropertiesAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return GetSettings(data, guildId).DefaultProperties.ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> AddPropertyAsync(ulong guildId, string property)
    {
        var normalized = CharacterSchema.Normalize(property);
        if (string.IsNullOrWhiteSpace(normalized) || CharacterSchema.ReservedProperties.Contains(normalized)) return false;

        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetSettings(data, guildId);
            if (settings.DefaultProperties.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return false;
            settings.DefaultProperties.Add(normalized);
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RemovePropertyAsync(ulong guildId, string property)
    {
        var normalized = CharacterSchema.Normalize(property);
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetSettings(data, guildId);
            var existing = settings.DefaultProperties.FirstOrDefault(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (existing is null) return false;
            settings.DefaultProperties.Remove(existing);
            settings.AutofillValues.Remove(existing);
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> AddAutofillAsync(ulong guildId, string field, string value)
    {
        var normalized = CharacterSchema.Normalize(field);
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetSettings(data, guildId);
            if (!settings.DefaultProperties.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                settings.DefaultProperties.Add(normalized);
            if (!settings.AutofillValues.TryGetValue(normalized, out var values))
                settings.AutofillValues[normalized] = values = [];
            if (values.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
            values.Add(value.Trim());
            values.Sort(StringComparer.OrdinalIgnoreCase);
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RemoveAutofillAsync(ulong guildId, string field, string value)
    {
        var normalized = CharacterSchema.Normalize(field);
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetSettings(data, guildId);
            if (!settings.AutofillValues.TryGetValue(normalized, out var values)) return false;
            var existing = values.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (existing is null) return false;
            values.Remove(existing);
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetAutofillAsync(ulong guildId, string field)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetSettings(data, guildId);
            return settings.AutofillValues.TryGetValue(CharacterSchema.Normalize(field), out var values)
                ? values.ToArray()
                : [];
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, CharacterGuildSettings>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        var json = await File.ReadAllTextAsync(_filePath);
        if (string.IsNullOrWhiteSpace(json)) return [];

        using var document = JsonDocument.Parse(json);
        var firstServer = document.RootElement.EnumerateObject().FirstOrDefault();
        var isCurrentFormat = firstServer.Value.ValueKind != JsonValueKind.Object ||
                              firstServer.Value.TryGetProperty("defaultProperties", out _) ||
                              firstServer.Value.TryGetProperty("autofillValues", out _);
        if (isCurrentFormat)
            return JsonSerializer.Deserialize<Dictionary<string, CharacterGuildSettings>>(json, JsonOptions) ?? [];

        // Migrates the earlier server -> command -> field -> values format.
        var legacy = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, List<string>>>>>(json, JsonOptions) ?? [];
        var migrated = new Dictionary<string, CharacterGuildSettings>();
        foreach (var (serverId, commands) in legacy)
        {
            var settings = new CharacterGuildSettings();
            if (commands.TryGetValue("approve", out var fields))
            {
                foreach (var (field, values) in fields)
                {
                    var normalized = CharacterSchema.Normalize(field);
                    if (!settings.DefaultProperties.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                        settings.DefaultProperties.Add(normalized);
                    settings.AutofillValues[normalized] = values;
                }
            }
            migrated[serverId] = settings;
        }
        await SaveUnsafeAsync(migrated);
        Console.WriteLine("Migrated legacy character settings to the editable property schema.");
        return migrated;
    }

    private async Task SaveUnsafeAsync(Dictionary<string, CharacterGuildSettings> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static CharacterGuildSettings GetSettings(Dictionary<string, CharacterGuildSettings> data, ulong guildId)
    {
        if (!data.TryGetValue(guildId.ToString(), out var settings))
            data[guildId.ToString()] = settings = new CharacterGuildSettings();
        return settings;
    }
}
