using System.Text.Json.Serialization;

namespace Yoko.Bot.Models;

internal sealed class SceneGuildData
{
    public List<SceneRecord> Scenes { get; set; } = [];
    public List<PendingSceneInvite> PendingInvites { get; set; } = [];
    public SceneSettings Settings { get; set; } = new();
    public long NextSceneNumber { get; set; } = 1;
}

internal sealed class SceneSettings
{
    public int ActiveLimitPerCharacter { get; set; } = 4;
    public Dictionary<ulong, int> ExtraSlotsByUser { get; set; } = [];
    public string? ReplyName { get; set; }
    public string AcceptanceMessage { get; set; } = "{user} accepted. **{charactername}** joined scene **{scene}**.";
}

internal sealed record SceneCapacity(int Active, int BaseLimit, int ExtraSlots)
{
    public int Limit => BaseLimit + ExtraSlots;
    public bool IsFull => Active >= Limit;
}

internal sealed record SceneCreateResult(SceneRecord? Scene, SceneCapacity Capacity);

internal sealed class SceneRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public long Number { get; set; }
    public WorldDate WorldDate { get; set; } = new();
    public ulong CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<SceneParticipant> Participants { get; set; } = [];

    [JsonIgnore]
    public bool IsCompleted => CompletedAt is not null;
}

internal sealed class SceneParticipant
{
    public ulong UserId { get; set; }
    public List<string> Characters { get; set; } = [];
    public Dictionary<string, Guid> CharacterIds { get; set; } = [];
}

internal sealed class PendingSceneInvite
{
    public ulong InvitationMessageId { get; set; }
    public ulong ChannelId { get; set; }
    public string SceneId { get; set; } = string.Empty;
    public ulong InvitedUserId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public Guid CharacterId { get; set; }
    public ulong InvitedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
