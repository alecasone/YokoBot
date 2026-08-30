using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class RelationshipStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public RelationshipStore(string filePath) => _filePath = filePath;

    public async Task<RelationshipMutationStatus> AddPendingAsync(
        ulong guildId,
        PendingRelationshipRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            var guild = GetGuild(data, guildId);
            if (guild.Relationships.Any(relationship => RelationshipCatalog.Equivalent(
                    relationship, request.SourceCharacterId, request.TargetCharacterId, request.TypeId)))
                return RelationshipMutationStatus.AlreadyExists;
            if (guild.PendingRequests.Any(pending => Equivalent(
                    pending, request.SourceCharacterId, request.TargetCharacterId, request.TypeId)))
                return RelationshipMutationStatus.RequestPending;

            guild.PendingRequests.Add(request);
            await SaveUnsafeAsync(data);
            return RelationshipMutationStatus.Success;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RelationshipRecord>> GetDirectAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return TryGetGuild(data, guildId, out var guild)
                ? guild!.Relationships.ToArray()
                : [];
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PendingRelationshipRequest>> GetRequestsForUserAsync(
        ulong guildId,
        ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return TryGetGuild(data, guildId, out var guild)
                ? guild!.PendingRequests
                    .Where(request => request.SourceOwnerId == userId || request.TargetOwnerId == userId)
                    .OrderBy(request => request.CreatedAt)
                    .ToArray()
                : [];
        }
        finally { _gate.Release(); }
    }

    public async Task<PendingRelationshipRequest?> GetPendingByMessageAsync(
        ulong guildId,
        ulong invitationMessageId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return TryGetGuild(data, guildId, out var guild)
                ? guild!.PendingRequests.FirstOrDefault(request => request.InvitationMessageId == invitationMessageId)
                : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<PendingRelationshipRequest?> GetPendingAsync(ulong guildId, string requestId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            return TryGetGuild(data, guildId, out var guild)
                ? FindPending(guild!, requestId).Request
                : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<RelationshipActionResult> ApproveAsync(
        ulong guildId,
        string requestId,
        ulong actingUserId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetGuild(data, guildId, out var guild))
                return new(RelationshipMutationStatus.NotFound);
            var guildData = guild!;

            var match = FindPending(guildData, requestId);
            if (match.Ambiguous) return new(RelationshipMutationStatus.Ambiguous);
            if (match.Request is not { } request) return new(RelationshipMutationStatus.NotFound);
            if (request.TargetOwnerId != actingUserId)
                return new(RelationshipMutationStatus.NotAuthorized, request);

            if (guildData.Relationships.Any(relationship => RelationshipCatalog.Equivalent(
                    relationship, request.SourceCharacterId, request.TargetCharacterId, request.TypeId)))
            {
                guildData.PendingRequests.Remove(request);
                await SaveUnsafeAsync(data);
                return new(RelationshipMutationStatus.AlreadyExists, request);
            }

            var relationship = new RelationshipRecord
            {
                SourceCharacterId = request.SourceCharacterId,
                SourceOwnerId = request.SourceOwnerId,
                TargetCharacterId = request.TargetCharacterId,
                TargetOwnerId = request.TargetOwnerId,
                TypeId = request.TypeId,
                RequestedByUserId = request.SourceOwnerId
            };
            guildData.Relationships.Add(relationship);
            guildData.PendingRequests.Remove(request);
            await SaveUnsafeAsync(data);
            return new(RelationshipMutationStatus.Success, request, relationship);
        }
        finally { _gate.Release(); }
    }

    public async Task<RelationshipActionResult> DeclineAsync(
        ulong guildId,
        string requestId,
        ulong actingUserId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetGuild(data, guildId, out var guild))
                return new(RelationshipMutationStatus.NotFound);
            var guildData = guild!;

            var match = FindPending(guildData, requestId);
            if (match.Ambiguous) return new(RelationshipMutationStatus.Ambiguous);
            if (match.Request is not { } request) return new(RelationshipMutationStatus.NotFound);
            if (request.TargetOwnerId != actingUserId)
                return new(RelationshipMutationStatus.NotAuthorized, request);

            guildData.PendingRequests.Remove(request);
            await SaveUnsafeAsync(data);
            return new(RelationshipMutationStatus.Success, request);
        }
        finally { _gate.Release(); }
    }

    public async Task<RelationshipActionResult> RemoveAsync(
        ulong guildId,
        string relationshipId,
        Guid selectedCharacterId,
        ulong actingUserId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetGuild(data, guildId, out var guild))
                return new(RelationshipMutationStatus.NotFound);
            var guildData = guild!;

            var match = FindRelationship(guildData, relationshipId);
            if (match.Ambiguous) return new(RelationshipMutationStatus.Ambiguous);
            if (match.Relationship is not { } relationship)
                return new(RelationshipMutationStatus.NotFound);
            if (relationship.SourceCharacterId != selectedCharacterId &&
                relationship.TargetCharacterId != selectedCharacterId)
                return new(RelationshipMutationStatus.NotFound);
            if (relationship.SourceOwnerId != actingUserId && relationship.TargetOwnerId != actingUserId)
                return new(RelationshipMutationStatus.NotAuthorized);

            guildData.Relationships.Remove(relationship);
            await SaveUnsafeAsync(data);
            return new(RelationshipMutationStatus.Success, Relationship: relationship);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> RemoveForCharacterAsync(ulong guildId, Guid characterId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafeAsync();
            if (!TryGetGuild(data, guildId, out var guild)) return 0;
            var direct = guild!.Relationships.RemoveAll(relationship =>
                relationship.SourceCharacterId == characterId || relationship.TargetCharacterId == characterId);
            var pending = guild.PendingRequests.RemoveAll(request =>
                request.SourceCharacterId == characterId || request.TargetCharacterId == characterId);
            if (direct + pending == 0) return 0;
            if (guild.Relationships.Count == 0 && guild.PendingRequests.Count == 0)
                data.Remove(guildId.ToString());
            await SaveUnsafeAsync(data);
            return direct + pending;
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, RelationshipGuildData>> LoadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        var json = await File.ReadAllTextAsync(_filePath);
        return string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, RelationshipGuildData>>(json, JsonOptions) ?? [];
    }

    private async Task SaveUnsafeAsync(Dictionary<string, RelationshipGuildData> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static RelationshipGuildData GetGuild(
        IDictionary<string, RelationshipGuildData> data,
        ulong guildId)
    {
        if (!data.TryGetValue(guildId.ToString(), out var guild))
            data[guildId.ToString()] = guild = new RelationshipGuildData();
        return guild;
    }

    private static bool TryGetGuild(
        IReadOnlyDictionary<string, RelationshipGuildData> data,
        ulong guildId,
        out RelationshipGuildData? guild) => data.TryGetValue(guildId.ToString(), out guild);

    private static (PendingRelationshipRequest? Request, bool Ambiguous) FindPending(
        RelationshipGuildData guild,
        string requestId)
    {
        var exact = guild.PendingRequests.FirstOrDefault(request =>
            request.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return (exact, false);
        var matches = guild.PendingRequests
            .Where(request => request.Id.StartsWith(requestId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => (matches[0], false),
            > 1 => (null, true),
            _ => (null, false)
        };
    }

    private static (RelationshipRecord? Relationship, bool Ambiguous) FindRelationship(
        RelationshipGuildData guild,
        string relationshipId)
    {
        var exact = guild.Relationships.FirstOrDefault(relationship =>
            relationship.Id.Equals(relationshipId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return (exact, false);
        var matches = guild.Relationships
            .Where(relationship => relationship.Id.StartsWith(relationshipId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => (matches[0], false),
            > 1 => (null, true),
            _ => (null, false)
        };
    }

    private static bool Equivalent(
        PendingRelationshipRequest request,
        Guid sourceCharacterId,
        Guid targetCharacterId,
        string typeId)
    {
        if (request.SourceCharacterId == sourceCharacterId &&
            request.TargetCharacterId == targetCharacterId &&
            request.TypeId.Equals(typeId, StringComparison.OrdinalIgnoreCase))
            return true;
        var inverse = RelationshipCatalog.Get(typeId)?.InverseId;
        return inverse is not null &&
               request.SourceCharacterId == targetCharacterId &&
               request.TargetCharacterId == sourceCharacterId &&
               request.TypeId.Equals(inverse, StringComparison.OrdinalIgnoreCase);
    }
}
