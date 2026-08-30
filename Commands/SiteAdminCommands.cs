using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Yoko.Bot.Services;

namespace Yoko.Bot.Commands;

internal static partial class SiteAdminCommands
{
    public static ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName("siteadmin")
            .WithDescription("Configures and publishes Yoko's public GitHub Pages directory.")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("setup")
                .WithDescription("Sets the GitHub repository and Pages locations.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("repository", ApplicationCommandOptionType.String, "owner/repository or its GitHub URL", isRequired: true)
                .AddOption("branch", ApplicationCommandOptionType.String, "Publishing branch; defaults to pages", isRequired: false)
                .AddOption("data-path", ApplicationCommandOptionType.String, "JSON path under docs/data", isRequired: false)
                .AddOption("base-url", ApplicationCommandOptionType.String, "Published Pages URL", isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("autopublish")
                .WithDescription("Enables or disables publishing after character changes.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("enabled", ApplicationCommandOptionType.Boolean, "Whether character changes publish automatically", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("publish")
                .WithDescription("Publishes a complete sanitized character snapshot now.")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("status")
                .WithDescription("Shows site configuration and synchronization status.")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();

    public static async Task HandleAsync(
        SocketSlashCommand command,
        SiteSettingsStore settingsStore,
        SitePublicationService publisher)
    {
        if (command.GuildId is not { } guildId)
        {
            await command.RespondAsync("Site administration can only be used in a server.", ephemeral: true);
            return;
        }

        var subcommand = command.Data.Options.First();
        switch (subcommand.Name)
        {
            case "setup":
                await SetupAsync(command, guildId, subcommand, settingsStore, publisher);
                break;
            case "autopublish":
                var enabled = (bool)Option(subcommand.Options, "enabled").Value;
                await publisher.SetAutoPublishAsync(guildId, enabled);
                var tokenNotice = enabled && !publisher.HasToken
                    ? " `GITHUB_PAGES_TOKEN` is currently missing, so publishes will remain pending."
                    : string.Empty;
                await command.RespondAsync(
                    $"Automatic Pages publishing is now **{(enabled ? "enabled" : "disabled")}**.{tokenNotice}",
                    ephemeral: true);
                break;
            case "publish":
                await command.DeferAsync(ephemeral: true);
                var result = await publisher.PublishNowAsync(guildId);
                await command.ModifyOriginalResponseAsync(message => message.Content = result.Success
                    ? $"Published the character directory successfully.{CommitNotice(result.CommitSha)}"
                    : $"Publish failed: {result.Message}");
                break;
            case "status":
                await ShowStatusAsync(command, await settingsStore.GetAsync(guildId), publisher.HasToken);
                break;
        }
    }

    private static async Task SetupAsync(
        SocketSlashCommand command,
        ulong guildId,
        SocketSlashCommandDataOption subcommand,
        SiteSettingsStore settingsStore,
        SitePublicationService publisher)
    {
        var repositoryInput = (string)Option(subcommand.Options, "repository").Value;
        if (!TryParseRepository(repositoryInput, out var owner, out var repository))
        {
            await command.RespondAsync("Use `owner/repository` or a normal `https://github.com/owner/repository` URL.", ephemeral: true);
            return;
        }

        var branch = OptionalString(subcommand.Options, "branch")?.Trim() ?? "pages";
        if (!ValidBranchPattern().IsMatch(branch) || branch.Contains("..", StringComparison.Ordinal))
        {
            await command.RespondAsync("That branch name is not safe or valid for this publisher.", ephemeral: true);
            return;
        }

        var dataPath = (OptionalString(subcommand.Options, "data-path") ?? "docs/data/characters.json")
            .Replace('\\', '/')
            .Trim('/');
        if (!dataPath.StartsWith("docs/data/", StringComparison.OrdinalIgnoreCase) ||
            !dataPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            dataPath.Split('/').Any(segment => segment is "." or ".." || string.IsNullOrWhiteSpace(segment)))
        {
            await command.RespondAsync("The data path must be a `.json` file beneath `docs/data/`.", ephemeral: true);
            return;
        }

        var defaultUrl = $"https://{owner.ToLowerInvariant()}.github.io/{repository}/";
        var baseUrl = OptionalString(subcommand.Options, "base-url")?.Trim() ?? defaultUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedUrl) || parsedUrl.Scheme is not ("http" or "https"))
        {
            await command.RespondAsync("The base URL must be an absolute `https://` or `http://` address.", ephemeral: true);
            return;
        }
        baseUrl = parsedUrl.AbsoluteUri.TrimEnd('/') + "/";

        await settingsStore.ConfigureAsync(guildId, owner, repository, branch, dataPath, baseUrl);
        await publisher.QueueAsync(guildId);
        var tokenStatus = publisher.HasToken ? "loaded" : "missing";
        await command.RespondAsync(
            $"Pages publishing configured.\n" +
            $"- Repository: `{owner}/{repository}`\n" +
            $"- Branch: `{branch}`\n" +
            $"- Data file: `{dataPath}`\n" +
            $"- Site: {baseUrl}\n" +
            $"- GitHub token: **{tokenStatus}**\n\n" +
            "Run `/siteadmin publish` for the first snapshot, then enable `/siteadmin autopublish` when ready.",
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    private static Task ShowStatusAsync(SocketSlashCommand command, Yoko.Bot.Models.SiteGuildSettings settings, bool hasToken)
    {
        var repository = settings.IsConfigured
            ? $"`{settings.RepositoryOwner}/{settings.RepositoryName}`"
            : "Not configured";
        var content = "**GitHub Pages publishing**\n" +
                      $"- Repository: {repository}\n" +
                      $"- Branch: `{settings.Branch}`\n" +
                      $"- Data file: `{settings.DataPath}`\n" +
                      $"- Site URL: {settings.BaseUrl ?? "Not configured"}\n" +
                      $"- Automatic publishing: **{(settings.AutoPublish ? "enabled" : "disabled")}**\n" +
                      $"- Local changes pending: **{(settings.PendingChanges ? "yes" : "no")}**\n" +
                      $"- GitHub token: **{(hasToken ? "loaded" : "missing")}**\n" +
                      $"- Last attempt: {Timestamp(settings.LastAttemptAt)}\n" +
                      $"- Last success: {Timestamp(settings.LastPublishedAt)}\n" +
                      $"- Last commit: {ShortSha(settings.LastCommitSha)}" +
                      (string.IsNullOrWhiteSpace(settings.LastError) ? string.Empty : $"\n- Last error: `{settings.LastError}`");
        return command.RespondAsync(content, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    private static bool TryParseRepository(string input, out string owner, out string repository)
    {
        var normalized = input.Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            normalized = uri.AbsolutePath.Trim('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^4];
        var pieces = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        owner = pieces.Length == 2 ? pieces[0] : string.Empty;
        repository = pieces.Length == 2 ? pieces[1] : string.Empty;
        return pieces.Length == 2 && RepositoryPartPattern().IsMatch(owner) && RepositoryPartPattern().IsMatch(repository);
    }

    private static string CommitNotice(string? sha) => string.IsNullOrWhiteSpace(sha)
        ? string.Empty
        : $" Commit: `{sha[..Math.Min(8, sha.Length)]}`.";

    private static string Timestamp(DateTimeOffset? timestamp) => timestamp is null
        ? "Never"
        : $"<t:{timestamp.Value.ToUnixTimeSeconds()}:R>";

    private static string ShortSha(string? sha) => string.IsNullOrWhiteSpace(sha)
        ? "None"
        : $"`{sha[..Math.Min(8, sha.Length)]}`";

    private static string? OptionalString(IReadOnlyCollection<SocketSlashCommandDataOption> options, string name) =>
        options.FirstOrDefault(option => option.Name == name)?.Value as string;

    private static SocketSlashCommandDataOption Option(
        IReadOnlyCollection<SocketSlashCommandDataOption> options,
        string name) => options.First(option => option.Name == name);

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPartPattern();

    [GeneratedRegex("^[A-Za-z0-9._/-]+$")]
    private static partial Regex ValidBranchPattern();
}
