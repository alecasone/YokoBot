namespace Yoko.Bot.Models;

internal sealed class VerificationGuildSettings
{
    public ulong? SuccessChannelId { get; set; }
    public string? SuccessMessage { get; set; }
    public Dictionary<string, VerificationProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["writer"] = new VerificationProfile(),
        ["spectator"] = new VerificationProfile()
    };
}

internal sealed class VerificationProfile
{
    public List<ulong> AddedRoleIds { get; set; } = [];
    public List<ulong> RemovedRoleIds { get; set; } = [];
}
