using System.Text.RegularExpressions;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal static class SceneDialogue
{
    public static string ReplyName(SceneSettings settings, string botName) =>
        string.IsNullOrWhiteSpace(settings.ReplyName) ? botName : settings.ReplyName;

    public static bool IsReply(string text, string verb, string replyName) =>
        Normalize(text) == Normalize(verb) || Normalize(text) == Normalize($"{verb} {replyName}");

    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[\s,.!]+", " ").Trim();

    public static string Render(string template, string user, string character, string scene, string date) =>
        Regex.Replace(template, @"@?\{user\}|\{charactername\}|\{scene\}|\{date\}", match => match.Value.ToLowerInvariant() switch
        {
            "{user}" or "@{user}" => user,
            "{charactername}" => character,
            "{scene}" => scene,
            "{date}" => date,
            _ => match.Value
        }, RegexOptions.IgnoreCase);

    public static bool IsValidTemplate(string template) => !string.IsNullOrWhiteSpace(template) &&
        template.Length <= 1500 && Render(template, "<@18446744073709551615>", new string('C', 100),
            new string('S', 100), new string('D', 30)).Length <= 2000;
}
