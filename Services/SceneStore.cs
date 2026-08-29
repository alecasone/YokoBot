using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class SceneStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public SceneStore(string filePath) => _filePath = filePath;

    public async Task<SceneRecord> CreateAsync(
        ulong guildId,
        ulong creatorId,
        string characterName,
        WorldDate worldDate,
        string title)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var guild = GetGuild(data, guildId);
            var scene = new SceneRecord
            {
                Title = title.Trim(),
                WorldDate = worldDate,
                CreatedBy = creatorId,
                Participants =
                [
                    new SceneParticipant
                    {
                        UserId = creatorId,
                        Characters = [characterName]
                    }
                ]
            };
            guild.Scenes.Add(scene);
            await SaveUnsafeAsync(data);
            return scene;
        }
        finally { _gate.Release(); }
    }

    public async Task<SceneRecord?> GetAsync(ulong guildId, string sceneId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return TryGetGuild(data, guildId, out var guild) ? Find(guild!, sceneId) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SceneRecord>> GetActiveAsync(ulong guildId)
    {
        var scenes = await GetAllAsync(guildId);
        return scenes.Where(scene => !scene.IsCompleted).ToArray();
    }

    public async Task<IReadOnlyList<SceneRecord>> GetAllAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return TryGetGuild(data, guildId, out var guild)
                ? guild!.Scenes
                    .OrderBy(scene => scene.WorldDate.Year)
                    .ThenBy(scene => scene.WorldDate.Month)
                    .ThenBy(scene => scene.WorldDate.Day)
                    .ThenBy(scene => scene.CreatedAt)
                    .ToArray()
                : [];
        }
        finally { _gate.Release(); }
    }

    public async Task<SceneMutationStatus> AddCharacterAsync(
        ulong guildId,
        string sceneId,
        ulong userId,
        string characterName)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetActive(data, guildId, sceneId, out var scene)) return SceneMutationStatus.NotFound;
            var participant = scene!.Participants.FirstOrDefault(item => item.UserId == userId);
            if (participant is null)
            {
                participant = new SceneParticipant { UserId = userId };
                scene.Participants.Add(participant);
            }
            if (participant.Characters.Contains(characterName, StringComparer.OrdinalIgnoreCase))
                return SceneMutationStatus.AlreadyExists;
            participant.Characters.Add(characterName);
            await SaveUnsafeAsync(data);
            return SceneMutationStatus.Success;
        }
        finally { _gate.Release(); }
    }

    public async Task<SceneMutationStatus> AddPendingInviteAsync(
        ulong guildId,
        PendingSceneInvite invite)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetActive(data, guildId, invite.SceneId, out var scene)) return SceneMutationStatus.NotFound;
            if (scene!.Participants.Any(participant =>
                    participant.UserId == invite.InvitedUserId &&
                    participant.Characters.Contains(invite.CharacterName, StringComparer.OrdinalIgnoreCase)))
                return SceneMutationStatus.AlreadyExists;

            var guild = GetGuild(data, guildId);
            if (guild.PendingInvites.Any(pending =>
                    pending.SceneId.Equals(invite.SceneId, StringComparison.OrdinalIgnoreCase) &&
                    pending.InvitedUserId == invite.InvitedUserId &&
                    pending.CharacterName.Equals(invite.CharacterName, StringComparison.OrdinalIgnoreCase)))
                return SceneMutationStatus.InvitePending;

            guild.PendingInvites.Add(invite);
            await SaveUnsafeAsync(data);
            return SceneMutationStatus.Success;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HasPendingInviteAsync(
        ulong guildId,
        string sceneId,
        ulong userId,
        string characterName)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return TryGetGuild(data, guildId, out var guild) && guild!.PendingInvites.Any(invite =>
                invite.SceneId.Equals(sceneId, StringComparison.OrdinalIgnoreCase) &&
                invite.InvitedUserId == userId &&
                invite.CharacterName.Equals(characterName, StringComparison.OrdinalIgnoreCase));
        }
        finally { _gate.Release(); }
    }

    public async Task<PendingSceneInvite?> GetPendingInviteAsync(ulong guildId, ulong invitationMessageId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return TryGetGuild(data, guildId, out var guild)
                ? guild!.PendingInvites.FirstOrDefault(invite => invite.InvitationMessageId == invitationMessageId)
                : null;
        }
        finally { _gate.Release(); }
    }

    public async Task RemovePendingInviteAsync(ulong guildId, ulong invitationMessageId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetGuild(data, guildId, out var guild)) return;
            var removed = guild!.PendingInvites.RemoveAll(invite => invite.InvitationMessageId == invitationMessageId);
            if (removed == 0) return;
            if (guild.Scenes.Count == 0 && guild.PendingInvites.Count == 0) data.Remove(guildId.ToString());
            await SaveUnsafeAsync(data);
        }
        finally { _gate.Release(); }
    }

    public async Task<SceneMutationStatus> RemoveCharacterAsync(
        ulong guildId,
        string sceneId,
        ulong userId,
        string characterName)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetActive(data, guildId, sceneId, out var scene)) return SceneMutationStatus.NotFound;
            var participant = scene!.Participants.FirstOrDefault(item => item.UserId == userId);
            if (participant is null) return SceneMutationStatus.ParticipantNotFound;
            var storedName = participant.Characters.FirstOrDefault(name =>
                name.Equals(characterName, StringComparison.OrdinalIgnoreCase));
            if (storedName is null) return SceneMutationStatus.CharacterNotFound;
            participant.Characters.Remove(storedName);
            if (participant.Characters.Count == 0) scene.Participants.Remove(participant);
            await SaveUnsafeAsync(data);
            return SceneMutationStatus.Success;
        }
        finally { _gate.Release(); }
    }

    public async Task<SceneMutationStatus> RemoveUserAsync(ulong guildId, string sceneId, ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetActive(data, guildId, sceneId, out var scene)) return SceneMutationStatus.NotFound;
            var removed = scene!.Participants.RemoveAll(item => item.UserId == userId);
            if (removed == 0) return SceneMutationStatus.ParticipantNotFound;
            await SaveUnsafeAsync(data);
            return SceneMutationStatus.Success;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> CompleteAsync(ulong guildId, string sceneId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetActive(data, guildId, sceneId, out var scene)) return false;
            scene!.CompletedAt = DateTimeOffset.UtcNow;
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(ulong guildId, string sceneId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetGuild(data, guildId, out var guild) ||
                Find(guild!, sceneId) is not { IsCompleted: false } scene)
                return false;
            guild!.Scenes.Remove(scene);
            if (guild.Scenes.Count == 0 && guild.PendingInvites.Count == 0) data.Remove(guildId.ToString());
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, SceneGuildData>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        var json = await File.ReadAllTextAsync(_filePath);
        return string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, SceneGuildData>>(json, JsonOptions) ?? [];
    }

    private async Task SaveUnsafeAsync(Dictionary<string, SceneGuildData> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static SceneGuildData GetGuild(Dictionary<string, SceneGuildData> data, ulong guildId)
    {
        if (!data.TryGetValue(guildId.ToString(), out var guild))
            data[guildId.ToString()] = guild = new SceneGuildData();
        return guild;
    }

    private static bool TryGetGuild(
        Dictionary<string, SceneGuildData> data,
        ulong guildId,
        out SceneGuildData? guild) =>
        data.TryGetValue(guildId.ToString(), out guild);

    private static bool TryGetActive(
        Dictionary<string, SceneGuildData> data,
        ulong guildId,
        string sceneId,
        out SceneRecord? scene)
    {
        scene = TryGetGuild(data, guildId, out var guild) ? Find(guild!, sceneId) : null;
        return scene is { IsCompleted: false };
    }

    private static SceneRecord? Find(SceneGuildData guild, string sceneId)
    {
        var exact = guild.Scenes.FirstOrDefault(scene =>
            scene.Id.Equals(sceneId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var prefixMatches = guild.Scenes
            .Where(scene => scene.Id.StartsWith(sceneId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return prefixMatches.Length == 1 ? prefixMatches[0] : null;
    }
}

internal enum SceneMutationStatus
{
    Success,
    NotFound,
    AlreadyExists,
    ParticipantNotFound,
    CharacterNotFound,
    InvitePending
}
