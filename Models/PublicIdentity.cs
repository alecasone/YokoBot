namespace Yoko.Bot.Models;

internal sealed class PublicUserIdentity
{
    public Guid PublicId { get; set; }
    public List<string> Aliases { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? DisplayName { get; set; }
    public bool HideDiscordId { get; set; }
}
