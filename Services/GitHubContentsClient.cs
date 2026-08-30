using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class GitHubContentsClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _tokenProvider;

    public GitHubContentsClient(HttpClient? httpClient = null, Func<string?>? tokenProvider = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _tokenProvider = tokenProvider ?? (() => Environment.GetEnvironmentVariable("GITHUB_PAGES_TOKEN"));
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(_tokenProvider());

    public async Task<SitePublishResult> PublishAsync(SiteGuildSettings settings, string json)
    {
        var token = _tokenProvider();
        if (string.IsNullOrWhiteSpace(token))
            return new SitePublishResult(false, "`GITHUB_PAGES_TOKEN` is not set for the bot process.");
        if (!settings.IsConfigured)
            return new SitePublishResult(false, "The Pages repository is not configured.");

        try
        {
            var endpoint = ContentEndpoint(settings);
            var existingSha = await GetExistingShaAsync(endpoint, settings.Branch, token);
            var body = new Dictionary<string, object?>
            {
                ["message"] = $"Publish Yoko character directory ({DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC)",
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)),
                ["branch"] = settings.Branch
            };
            if (existingSha is not null) body["sha"] = existingSha;

            using var request = CreateRequest(HttpMethod.Put, endpoint, token);
            request.Content = JsonContent.Create(body);
            using var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return new SitePublishResult(false, GitHubError("GitHub rejected the publish", response, responseBody));

            using var document = JsonDocument.Parse(responseBody);
            var commitSha = document.RootElement.TryGetProperty("commit", out var commit) &&
                            commit.TryGetProperty("sha", out var sha)
                ? sha.GetString()
                : null;
            return new SitePublishResult(true, "The sanitized character directory was committed to GitHub.", commitSha);
        }
        catch (TaskCanceledException)
        {
            return new SitePublishResult(false, "The GitHub request timed out.");
        }
        catch (Exception exception)
        {
            return new SitePublishResult(false, $"GitHub publish failed: {exception.Message}");
        }
    }

    private async Task<string?> GetExistingShaAsync(string endpoint, string branch, string token)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{endpoint}?ref={Uri.EscapeDataString(branch)}", token);
        using var response = await _httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(GitHubError("GitHub could not read the current site data", response, responseBody));
        using var document = JsonDocument.Parse(responseBody);
        return document.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("YokoBot/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }

    private static string ContentEndpoint(SiteGuildSettings settings)
    {
        var path = string.Join('/', settings.DataPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return $"https://api.github.com/repos/{Uri.EscapeDataString(settings.RepositoryOwner!)}/" +
               $"{Uri.EscapeDataString(settings.RepositoryName!)}/contents/{path}";
    }

    private static string GitHubError(string prefix, HttpResponseMessage response, string body)
    {
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var value)) message = value.GetString();
        }
        catch (JsonException) { }
        message ??= response.ReasonPhrase ?? "unknown error";
        if (message.Length > 350) message = message[..347] + "...";
        return $"{prefix} ({(int)response.StatusCode}): {message}";
    }
}

