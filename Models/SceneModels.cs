using System.Text.Json.Serialization;

namespace Yoko.Bot.Models;

internal sealed class SceneGuildData
{
    public List<SceneRecord> Scenes { get; set; } = [];
    public List<PendingSceneInvite> PendingInvites { get; set; } = [];
}

internal sealed class SceneRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
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
}

internal sealed class PendingSceneInvite
{
    public ulong InvitationMessageId { get; set; }
    public ulong ChannelId { get; set; }
    public string SceneId { get; set; } = string.Empty;
    public ulong InvitedUserId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public ulong InvitedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
