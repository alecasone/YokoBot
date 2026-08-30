namespace Yoko.Bot.Models;

internal sealed record RelationshipDefinition(
    string Id,
    string DisplayName,
    string InverseId,
    string Category,
    bool Requestable,
    IReadOnlyList<string> Aliases);

internal sealed record RelationshipInferenceRule(
    string Id,
    IReadOnlyList<string> PathTypeIds,
    string ResultTypeId,
    string Explanation);

internal sealed class RelationshipGuildData
{
    public List<RelationshipRecord> Relationships { get; set; } = [];
    public List<PendingRelationshipRequest> PendingRequests { get; set; } = [];
}

internal sealed class RelationshipRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Guid SourceCharacterId { get; set; }
    public ulong SourceOwnerId { get; set; }
    public Guid TargetCharacterId { get; set; }
    public ulong TargetOwnerId { get; set; }
    public string TypeId { get; set; } = string.Empty;
    public ulong RequestedByUserId { get; set; }
    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class PendingRelationshipRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ulong InvitationMessageId { get; set; }
    public ulong ChannelId { get; set; }
    public Guid SourceCharacterId { get; set; }
    public ulong SourceOwnerId { get; set; }
    public Guid TargetCharacterId { get; set; }
    public ulong TargetOwnerId { get; set; }
    public string TypeId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed record RelationshipEdge(
    Guid SourceCharacterId,
    Guid TargetCharacterId,
    string TypeId,
    bool IsInferred,
    string? RelationshipId,
    string? RuleId,
    string? Explanation);

internal sealed record OwnedCharacter(ulong OwnerId, Character Character);

internal sealed record RelationshipActionResult(
    RelationshipMutationStatus Status,
    PendingRelationshipRequest? Request = null,
    RelationshipRecord? Relationship = null);

internal enum RelationshipMutationStatus
{
    Success,
    NotFound,
    NotAuthorized,
    AlreadyExists,
    RequestPending,
    Ambiguous
}
