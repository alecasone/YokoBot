using System.Text.Json;
using System.Text.RegularExpressions;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class UniverseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public UniverseStore(string filePath) => _filePath = filePath;

    public async Task<UniverseData> GetAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return data.TryGetValue(guildId.ToString(), out var universe) ? universe : new UniverseData();
        }
        finally { _gate.Release(); }
    }

    public async Task SetWorldDateAsync(ulong guildId, WorldDate worldDate)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!data.TryGetValue(guildId.ToString(), out var universe))
                data[guildId.ToString()] = universe = new UniverseData();
            universe.CurrentWorldDate = worldDate;
            await SaveUnsafeAsync(data);
        }
        finally { _gate.Release(); }
    }

    public static bool TryParseWorldDate(string input, out WorldDate? worldDate)
    {
        worldDate = null;
        var value = input.Trim();
        string[] parts;
        if (Regex.IsMatch(value, @"^\d{8}$"))
        {
            parts = [value[..2], value.Substring(2, 2), value.Substring(4, 4)];
        }
        else
        {
            parts = Regex.Split(value, @"[-/]").Select(part => part.Trim()).ToArray();
            if (parts.Length != 3 ||
                parts[0].Length is < 1 or > 2 ||
                parts[1].Length is < 1 or > 2 ||
                parts[2].Length != 4 ||
                parts.Any(part => !part.All(char.IsDigit)))
                return false;
        }

        if (!int.TryParse(parts[0], out var day) ||
            !int.TryParse(parts[1], out var month) ||
            !int.TryParse(parts[2], out var year) ||
            month is < 1 or > 12 ||
            year is < 1 or > 9999)
            return false;

        var candidate = new WorldDate { Day = day, Month = month, Year = year };
        if (!candidate.IsValidDay(day)) return false;
        worldDate = candidate;
        return true;
    }

    private async Task<Dictionary<string, UniverseData>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        var json = await File.ReadAllTextAsync(_filePath);
        return string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, UniverseData>>(json, JsonOptions) ?? [];
    }

    private async Task SaveUnsafeAsync(Dictionary<string, UniverseData> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }
}
