using System.Text.Json;

namespace Yoko.Bot.Models;

internal sealed class SiteGuildSettings
{
    public string? RepositoryOwner { get; set; }
    public string? RepositoryName { get; set; }
    public string Branch { get; set; } = "pages";
    public string DataPath { get; set; } = "docs/data/characters.json";
    public string? BaseUrl { get; set; }
    public bool AutoPublish { get; set; }
    public bool PendingChanges { get; set; }
    public long ChangeVersion { get; set; }
    public long LastPublishedVersion { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastPublishedAt { get; set; }
    public string? LastCommitSha { get; set; }
    public string? LastError { get; set; }
    public SiteBranding? Branding { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(RepositoryOwner) &&
        !string.IsNullOrWhiteSpace(RepositoryName) &&
        !string.IsNullOrWhiteSpace(DataPath);
}

internal sealed class PublicCharacterSnapshot
{
    public int SchemaVersion { get; set; } = 4;
    public SiteBranding? Branding { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PublicCharacterRecord> Characters { get; set; } = [];
    public List<PublicRelationshipTypeRecord> RelationshipTypes { get; set; } = [];
    public List<PublicRelationshipRecord> Relationships { get; set; } = [];
}

internal sealed class SiteBranding
{
    public string Name { get; set; } = "Yoko";
    public string LogoLetter { get; set; } = "Y";
    public string ArchiveTitle { get; set; } = "Character Archive";
    public string AtlasTitle { get; set; } = "Relationship Atlas";
    public string Tagline { get; set; } = "An index of approved lives, legends, and questionable alibis.";
}

internal sealed class PublicCharacterRecord
{
    public Guid PublicId { get; set; }
    public required string Name { get; set; }
    public PublicCharacterOwner? Owner { get; set; }
    public List<string> Aliases { get; set; } = [];
    public string? Age { get; set; }
    public string? Gender { get; set; }
    public string? Region { get; set; }
    public string? Occupation { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
    public PublicCharacterReference? Reference { get; set; }
    public Dictionary<string, JsonElement> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PublicCharacterOwner
{
    public required string DisplayName { get; set; }
    // Snowflakes must stay strings: JavaScript numbers cannot represent them exactly.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DiscordId { get; set; }
}

internal sealed class PublicCharacterReference
{
    public string Kind { get; set; } = "sheet";
    public string Format { get; set; } = "link";
    public string? Value { get; set; }
}

internal sealed class PublicRelationshipTypeRecord
{
    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public required string Category { get; set; }
    public string? InverseId { get; set; }
}

internal sealed class PublicRelationshipRecord
{
    public Guid SourceCharacterId { get; set; }
    public Guid TargetCharacterId { get; set; }
    public required string TypeId { get; set; }
    public required string DisplayName { get; set; }
    public required string Category { get; set; }
    public bool IsInferred { get; set; }
    public string? Explanation { get; set; }
}

internal sealed record SitePublishResult(bool Success, string Message, string? CommitSha = null);
