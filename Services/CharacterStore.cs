using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class CharacterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public CharacterStore(string filePath) => _filePath = filePath;

    public async Task<Character?> AddAsync(ulong guildId, ulong userId, string name, ulong approvedBy)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync(guildId);
            var server = GetOrAddServer(data, guildId);
            if (!server.TryGetValue(userId.ToString(), out var user))
                server[userId.ToString()] = user = new UserCharacters();
            if (Find(user, name) is not null) return null;

            var character = new Character { Name = name.Trim(), ApprovedBy = approvedBy };
            user.Characters.Add(character);
            await SaveUnsafeAsync(data);
            return character;
        }
        finally { _gate.Release(); }
    }

    public async Task<Character?> GetAsync(ulong guildId, ulong userId, string name)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync(guildId);
            return TryGetUser(data, guildId, userId, out var user) ? Find(user!, name) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetCharacterNamesAsync(ulong guildId, ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync(guildId);
            return TryGetUser(data, guildId, userId, out var user)
                ? user!.Characters.Select(character => character.Name).OrderBy(name => name).ToArray()
                : [];
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetFieldNamesAsync(ulong guildId, ulong userId, string name)
    {
        string[] baseline = ["name", "age", "gender", "region", "occupation", "reference", "reference-kind", "reference-format"];
        var character = await GetAsync(guildId, userId, name);
        return character is null
            ? baseline
            : baseline.Concat(character.AdditionalProperties.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<bool> SetFieldAsync(ulong guildId, ulong userId, string name, string field, string value)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync(guildId);
            if (!TryGetUser(data, guildId, userId, out var user) || Find(user!, name) is not { } character)
                return false;

            switch (Normalize(field))
            {
                case "name": character.Name = value.Trim(); break;
                case "age": character.Age = value; break;
                case "gender": character.Gender = value; break;
                case "region": character.Region = value; break;
                case "occupation": character.Occupation = value; break;
                case "reference": character.CharacterReference.Value = value; break;
                case "referencekind": character.CharacterReference.Kind = value; break;
                case "referenceformat": character.CharacterReference.Format = value; break;
                default: character.AdditionalProperties[field.Trim()] = JsonSerializer.SerializeToElement(value); break;
            }

            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RemoveFieldAsync(ulong guildId, ulong userId, string name, string field)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync(guildId);
            if (!TryGetUser(data, guildId, userId, out var user) || Find(user!, name) is not { } character)
                return false;

            var removed = true;
            switch (Normalize(field))
            {
                case "age": character.Age = null; break;
                case "gender": character.Gender = null; break;
                case "region": character.Region = null; break;
                case "occupation": character.Occupation = null; break;
                case "reference": character.CharacterReference.Value = null; break;
                default: removed = character.AdditionalProperties.Remove(field.Trim()); break;
            }

            if (removed) await SaveUnsafeAsync(data);
            return removed;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(ulong guildId, ulong userId, string name)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync(guildId);
            if (!TryGetUser(data, guildId, userId, out var user) || Find(user!, name) is not { } character)
                return false;

            user!.Characters.Remove(character);
            var server = data[guildId.ToString()];
            if (user.Characters.Count == 0) server.Remove(userId.ToString());
            if (server.Count == 0) data.Remove(guildId.ToString());
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, Dictionary<string, UserCharacters>>> LoadUnsafeAsync(ulong migrationGuildId)
    {
        if (!File.Exists(_filePath)) return [];

        var json = await File.ReadAllTextAsync(_filePath);
        if (string.IsNullOrWhiteSpace(json)) return [];

        using var document = JsonDocument.Parse(json);
        var firstRecord = document.RootElement.EnumerateObject().FirstOrDefault();
        if (firstRecord.Value.ValueKind == JsonValueKind.Object && firstRecord.Value.TryGetProperty("characters", out _))
        {
            var legacy = JsonSerializer.Deserialize<Dictionary<string, UserCharacters>>(json, JsonOptions) ?? [];
            var migrated = new Dictionary<string, Dictionary<string, UserCharacters>>
            {
                [migrationGuildId.ToString()] = legacy
            };
            await SaveUnsafeAsync(migrated);
            Console.WriteLine($"Migrated legacy character data into server {migrationGuildId}.");
            return migrated;
        }

        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, UserCharacters>>>(json, JsonOptions) ?? [];
    }

    private async Task SaveUnsafeAsync(Dictionary<string, Dictionary<string, UserCharacters>> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static Dictionary<string, UserCharacters> GetOrAddServer(
        Dictionary<string, Dictionary<string, UserCharacters>> data,
        ulong guildId)
    {
        if (!data.TryGetValue(guildId.ToString(), out var server))
            data[guildId.ToString()] = server = [];
        return server;
    }

    private static bool TryGetUser(
        Dictionary<string, Dictionary<string, UserCharacters>> data,
        ulong guildId,
        ulong userId,
        out UserCharacters? user)
    {
        user = null;
        return data.TryGetValue(guildId.ToString(), out var server) && server.TryGetValue(userId.ToString(), out user);
    }

    private static Character? Find(UserCharacters user, string name) =>
        user.Characters.FirstOrDefault(character => character.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string field) =>
        new(field.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
