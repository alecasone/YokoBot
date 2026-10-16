using System.Globalization;
using System.Text.RegularExpressions;

namespace Yoko.Bot.Services;

internal static class ScheduleRules
{
    public const int MaxMessageLength = 2000;
    public const int MaxNameLength = 100;
    public static readonly TimeSpan MinimumTimespan = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumTimespan = TimeSpan.FromDays(365);

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength &&
        name.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    public static bool IsValidMode(string mode) =>
        mode is not null && (mode.Equals("pseudorandom", StringComparison.OrdinalIgnoreCase) ||
                             mode.Equals("incremental", StringComparison.OrdinalIgnoreCase) ||
                             mode.Equals("random", StringComparison.OrdinalIgnoreCase));

    public static bool TryParseTimespan(string text, out TimeSpan timespan)
    {
        timespan = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var matches = Regex.Matches(text, @"([0-9]+)([smhdw])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var consumed = 0;
        long seconds = 0;
        foreach (Match match in matches)
        {
            if (match.Index != consumed || !long.TryParse(match.Groups[1].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out var amount)) return false;
            var multiplier = char.ToLowerInvariant(match.Groups[2].Value[0]) switch
            {
                's' => 1L,
                'm' => 60L,
                'h' => 3600L,
                'd' => 86400L,
                'w' => 604800L,
                _ => 0L
            };
            // Check against the supported maximum before multiplying or adding.
            if (amount > ((long)MaximumTimespan.TotalSeconds - seconds) / multiplier) return false;
            seconds += amount * multiplier;
            consumed += match.Length;
        }
        if (consumed != text.Length || seconds < MinimumTimespan.TotalSeconds) return false;
        timespan = TimeSpan.FromSeconds(seconds);
        return true;
    }

    public static string FormatTimespan(TimeSpan timespan)
    {
        var seconds = (long)timespan.TotalSeconds;
        if (seconds <= 0) return "0s";
        var parts = new List<string>();
        foreach (var (unit, size) in new[] { ("w", 604800L), ("d", 86400L), ("h", 3600L), ("m", 60L), ("s", 1L) })
        {
            if (seconds >= size) parts.Add($"{seconds / size}{unit}");
            seconds %= size;
        }
        return string.Concat(parts);
    }
}
