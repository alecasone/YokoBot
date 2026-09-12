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
    private readonly CharacterStore _characters;

    public SceneStore(string filePath, CharacterStore characters)
    {
        _filePath = filePath;
        _characters = characters;
    }

    public async Task<SceneCreateResult> CreateAsync(
        ulong guildId,
        ulong creatorId,
        Character character,
        WorldDate worldDate,
        string? title)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var guild = GetGuild(data, guildId);
            var capacity = Capacity(guild, creatorId, character.PublicId);
            if (capacity.IsFull) return new SceneCreateResult(null, capacity);
            var number = guild.NextSceneNumber++;
            var scene = new SceneRecord
            {
                Number = number,
                Title = string.IsNullOrWhiteSpace(title) ? $"Scene #{number} — {worldDate.Display}" : title.Trim(),
                WorldDate = worldDate,
                CreatedBy = creatorId,
                Participants =
                [
                    new SceneParticipant
                    {
                        UserId = creatorId,
                        Characters = [character.Name],
                        CharacterIds = new() { [character.Name] = character.PublicId }
                    }
                ]
            };
            guild.Scenes.Add(scene);
            await SaveUnsafeAsync(data);
            return new SceneCreateResult(scene, capacity with { Active = capacity.Active + 1 });
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
        Character character)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetActive(data, guildId, sceneId, out var scene)) return SceneMutationStatus.NotFound;
            var guild = GetGuild(data, guildId);
            var participant = scene!.Participants.FirstOrDefault(item => item.UserId == userId);
            if (participant?.CharacterIds.ContainsValue(character.PublicId) == true)
                return SceneMutationStatus.AlreadyExists;
            if (Capacity(guild, userId, character.PublicId).IsFull) return SceneMutationStatus.AtCapacity;
            if (participant is null)
            {
                participant = new SceneParticipant { UserId = userId };
                scene.Participants.Add(participant);
            }
            if (participant.Characters.Contains(character.Name, StringComparer.OrdinalIgnoreCase))
                return SceneMutationStatus.AlreadyExists;
            participant.Characters.Add(character.Name);
            participant.CharacterIds[character.Name] = character.PublicId;
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
            if (Capacity(guild, invite.InvitedUserId, invite.CharacterId).IsFull)
                return SceneMutationStatus.AtCapacity;
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
            participant.CharacterIds.Remove(storedName);
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
            guild.PendingInvites.RemoveAll(invite => invite.SceneId == scene.Id);
            await SaveUnsafeAsync(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> RemoveCharactersAsync(ulong guildId, IReadOnlyList<OwnedCharacter> characters)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetGuild(data, guildId, out var guild)) return 0;
            var ids = characters.Select(item => item.Character.PublicId).ToHashSet();
            var legacyNames = characters.GroupBy(item => item.OwnerId).ToDictionary(group => group.Key,
                group => group.SelectMany(item => item.Character.Aliases.Append(item.Character.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase));
            bool Matches(ulong owner, string name, Guid id) => id != Guid.Empty
                ? ids.Contains(id) : legacyNames.GetValueOrDefault(owner)?.Contains(name) == true;
            var removed = 0;
            foreach (var scene in guild!.Scenes.ToArray())
            {
                var touched = false;
                foreach (var participant in scene.Participants.ToArray())
                {
                    foreach (var name in participant.Characters.ToArray())
                    {
                        if (!Matches(participant.UserId, name, participant.CharacterIds.GetValueOrDefault(name))) continue;
                        participant.Characters.Remove(name);
                        participant.CharacterIds.Remove(name);
                        removed++;
                        touched = true;
                    }
                    if (participant.Characters.Count == 0) scene.Participants.Remove(participant);
                }
                // Keep other characters' scenes/history; remove a scene made empty by this deletion.
                if (touched && scene.Participants.Count == 0)
                {
                    guild.Scenes.Remove(scene);
                    removed += guild.PendingInvites.RemoveAll(invite => invite.SceneId == scene.Id);
                }
            }
            removed += guild.PendingInvites.RemoveAll(invite => Matches(invite.InvitedUserId, invite.CharacterName, invite.CharacterId));
            if (removed > 0) await SaveUnsafeAsync(data);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, SceneGuildData>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        var json = await File.ReadAllTextAsync(_filePath);
        var data = string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, SceneGuildData>>(json, JsonOptions) ?? [];
        var changed = false;
        foreach (var (guildKey, guild) in data)
        {
            if (!ulong.TryParse(guildKey, out var guildId)) continue;
            var owned = await _characters.GetAllOwnedAsync(guildId);
            var byId = owned.ToDictionary(item => item.Character.PublicId);
            Character? Resolve(ulong owner, string name) => owned.FirstOrDefault(item =>
                item.OwnerId == owner && item.Character.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Character
                ?? owned.FirstOrDefault(item => item.OwnerId == owner &&
                    item.Character.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase))?.Character;
            guild.NextSceneNumber = Math.Max(guild.NextSceneNumber, guild.Scenes.Select(s => s.Number).DefaultIfEmpty().Max() + 1);
            foreach (var scene in guild.Scenes.OrderBy(s => s.CreatedAt))
            {
                if (scene.Number == 0) { scene.Number = guild.NextSceneNumber++; changed = true; }
                foreach (var participant in scene.Participants)
                foreach (var name in participant.Characters.ToArray())
                {
                    var known = participant.CharacterIds.TryGetValue(name, out var id);
                    var character = known ? byId.GetValueOrDefault(id)?.Character : Resolve(participant.UserId, name);
                    if (character is null) continue;
                    if (!known || name != character.Name)
                    {
                        participant.Characters.Remove(name);
                        participant.CharacterIds.Remove(name);
                        if (!participant.Characters.Contains(character.Name)) participant.Characters.Add(character.Name);
                        participant.CharacterIds[character.Name] = character.PublicId;
                        changed = true;
                    }
                }
            }
            foreach (var invite in guild.PendingInvites)
            {
                var character = invite.CharacterId == Guid.Empty ? Resolve(invite.InvitedUserId, invite.CharacterName)
                    : byId.GetValueOrDefault(invite.CharacterId)?.Character;
                if (character is null || (invite.CharacterId == character.PublicId && invite.CharacterName == character.Name)) continue;
                invite.CharacterId = character.PublicId;
                invite.CharacterName = character.Name;
                changed = true;
            }
        }
        if (changed) await SaveUnsafeAsync(data);
        return data;
    }

    private static SceneCapacity Capacity(SceneGuildData guild, ulong userId, Guid characterId) => new(
        guild.Scenes.Count(scene => !scene.IsCompleted && scene.Participants.Any(p =>
            p.UserId == userId && p.CharacterIds.ContainsValue(characterId))),
        guild.Settings.ActiveLimitPerCharacter,
        guild.Settings.ExtraSlotsByUser.GetValueOrDefault(userId));

    public async Task<SceneCapacity> GetCapacityAsync(ulong guildId, ulong userId, Guid characterId)
    {
        await _gate.WaitAsync();
        try { return Capacity(GetGuild(await LoadUnsafeAsync(), guildId), userId, characterId); }
        finally { _gate.Release(); }
    }

    public async Task<SceneSettings> GetSettingsAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try { return GetGuild(await LoadUnsafeAsync(), guildId).Settings; }
        finally { _gate.Release(); }
    }

    public async Task ConfigureAsync(ulong guildId, int? limit, string? replyName, string? acceptanceMessage)
    {
        if (limit is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        if (replyName?.Trim().Length is 0 or > 32) throw new ArgumentException("Reply name must be 1–32 characters.");
        if (acceptanceMessage?.Trim().Length is 0 or > 1500) throw new ArgumentException("Message must be 1–1,500 characters.");
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetGuild(data, guildId).Settings;
            if (limit is { } value) settings.ActiveLimitPerCharacter = value;
            if (replyName is not null) settings.ReplyName = replyName.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : replyName.Trim();
            if (acceptanceMessage is not null) settings.AcceptanceMessage = acceptanceMessage;
            await SaveUnsafeAsync(data);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> ChangeSlotsAsync(ulong guildId, ulong userId, int amount, bool reset = false)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var settings = GetGuild(data, guildId).Settings;
            var slots = reset ? 0 : Math.Clamp((long)settings.ExtraSlotsByUser.GetValueOrDefault(userId) + amount, 0, 1000);
            if (slots == 0) settings.ExtraSlotsByUser.Remove(userId);
            else settings.ExtraSlotsByUser[userId] = (int)slots;
            await SaveUnsafeAsync(data);
            return (int)slots;
        }
        finally { _gate.Release(); }
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
    InvitePending,
    AtCapacity
}
