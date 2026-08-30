using System.Text.Json;

namespace Yoko.Bot.Services;

internal sealed class LocalSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public string? DiscordBotToken { get; set; }
    public string? DiscordTestGuildId { get; set; }
    public string? DiscordDefaultChannelId { get; set; }
    public string? GitHubPagesToken { get; set; }

    public static LocalSettings LoadAndApply(string filePath)
    {
        var settings = Load(filePath);
        ValidateId(settings.DiscordTestGuildId, "discordTestGuildId");
        ValidateId(settings.DiscordDefaultChannelId, "discordDefaultChannelId");
        ApplyFallback("DISCORD_BOT_TOKEN", settings.DiscordBotToken);
        ApplyFallback("DISCORD_TEST_GUILD_ID", settings.DiscordTestGuildId);
        ApplyFallback("DISCORD_DEFAULT_CHANNEL_ID", settings.DiscordDefaultChannelId);
        ApplyFallback("GITHUB_PAGES_TOKEN", settings.GitHubPagesToken);
        return settings;
    }

    private static LocalSettings Load(string filePath)
    {
        if (!File.Exists(filePath)) return new LocalSettings();
        try
        {
            var json = File.ReadAllText(filePath);
            return string.IsNullOrWhiteSpace(json)
                ? new LocalSettings()
                : JsonSerializer.Deserialize<LocalSettings>(json, JsonOptions) ?? new LocalSettings();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Could not read {Path.GetFileName(filePath)}. Check its JSON formatting near line {exception.LineNumber}.",
                exception);
        }
    }

    private static void ApplyFallback(string environmentName, string? localValue)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentName)) ||
            string.IsNullOrWhiteSpace(localValue))
            return;
        Environment.SetEnvironmentVariable(environmentName, localValue.Trim());
    }

    private static void ValidateId(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!ulong.TryParse(value.Trim(), out _))
            throw new InvalidOperationException($"`{propertyName}` in local.settings.json must be a numeric Discord ID.");
    }
}
