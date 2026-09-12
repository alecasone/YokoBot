namespace Yoko.Bot.Models;

internal sealed class CharacterPurgeConfirmation(ulong guildId, ulong actorId, ulong channelId, ulong? ownerId,
    IReadOnlyCollection<Guid> ids, DateTimeOffset createdAt)
{
    public ulong GuildId { get; } = guildId;
    public ulong ActorId { get; } = actorId;
    public ulong ChannelId { get; } = channelId;
    public ulong? OwnerId { get; } = ownerId;
    public IReadOnlyCollection<Guid> CharacterIds { get; } = ids.ToArray();
    public DateTimeOffset ExpiresAt { get; } = createdAt.AddMinutes(5);
    public string Permission => OwnerId is null ? "character.purge.server" : "character.purge.user";
    public string TargetText => OwnerId is { } id ? $"DELETE USER {id}" : $"DELETE SERVER {GuildId}";
    public string FinalText { get; } = "CONFIRM " + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    public bool TargetConfirmed { get; private set; }
    public bool Finished { get; private set; }

    public PurgeConfirmationStep Reply(ulong guildId, ulong actorId, ulong channelId, string text, DateTimeOffset now, bool authorized)
    {
        if (guildId != GuildId || actorId != ActorId || channelId != ChannelId) return PurgeConfirmationStep.WrongActor;
        if (Finished || now >= ExpiresAt || !authorized) { Finished = true; return PurgeConfirmationStep.Expired; }
        var reply = text.Trim();
        if (new[] { "cancel", "stop", "end" }.Contains(reply, StringComparer.OrdinalIgnoreCase))
        { Finished = true; return PurgeConfirmationStep.Cancelled; }
        if (!TargetConfirmed)
        {
            if (!reply.Equals(TargetText, StringComparison.Ordinal)) return PurgeConfirmationStep.Mismatch;
            TargetConfirmed = true;
            return PurgeConfirmationStep.TargetConfirmed;
        }
        if (!reply.Equals(FinalText, StringComparison.Ordinal)) return PurgeConfirmationStep.Mismatch;
        Finished = true;
        return PurgeConfirmationStep.Execute;
    }
}

internal enum PurgeConfirmationStep { WrongActor, Expired, Cancelled, Mismatch, TargetConfirmed, Execute }
