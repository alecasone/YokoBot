using System.Text;
using System.Text.Json;
using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed record ManualFieldEdit(string Key, bool Custom, string? Value)
{
    public string Id => (Custom ? "custom:" : "builtin:") + Key;
    public string Label => Custom ? JsonSerializer.Serialize(Key) : CharacterManualEdit.Label(Key);
}

internal sealed record ManualEditResult(Character? Character, IReadOnlyList<string> Changes, string? Error);

internal static class CharacterManualEdit
{
    public const int MaxBytes = 256 * 1024;
    private static readonly string[] BuiltIns =
        ["name", "aliases", "age", "gender", "region", "occupation", "reference", "reference-kind", "reference-format"];
    private static readonly HashSet<string> Protected =
        ["publicid", "approvedat", "approvedby", "ocroleindex", "characterreference", "additionalproperties"];

    public static string Label(string key) => key switch
    {
        "name" => "Full Name",
        "reference" => "Link",
        _ => CharacterSchema.Label(key)
    };

    private static string Compact(string key) =>
        new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? BuiltIn(string key) => Compact(key) switch
    {
        "fullname" or "name" => "name",
        "link" or "reference" => "reference",
        "alias" or "aliases" => "aliases",
        "referencekind" => "reference-kind",
        "referenceformat" => "reference-format",
        "age" => "age",
        "gender" => "gender",
        "region" => "region",
        "occupation" => "occupation",
        _ => null
    };

    public static string Snapshot(Character character) => JsonSerializer.Serialize(character);

    public static string? Value(Character character, ManualFieldEdit field)
    {
        if (field.Custom)
        {
            var found = character.AdditionalProperties.FirstOrDefault(item =>
                item.Key.Equals(field.Key, StringComparison.OrdinalIgnoreCase));
            return found.Key is null ? null : found.Value.ToString();
        }
        return field.Key switch
        {
            "name" => character.Name,
            "aliases" => string.Join(", ", character.Aliases),
            "age" => character.Age,
            "gender" => character.Gender,
            "region" => character.Region,
            "occupation" => character.Occupation,
            "reference" => character.CharacterReference.Value,
            "reference-kind" => character.CharacterReference.Kind,
            "reference-format" => character.CharacterReference.Format,
            _ => null
        };
    }

    public static string Encode(string? value) =>
        value is null ? "" :
        value.Length == 0 || value.Trim() != value || value.Contains('\n') || value.Contains('\r') ||
        value.StartsWith('"') || value.Equals("[clear]", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(value) : value;

    public static string Template(Character character, string editId)
    {
        var text = new StringBuilder($"BEGIN CHARACTER EDIT\nEdit ID: {editId}\n\n");
        foreach (var key in BuiltIns)
        {
            var field = new ManualFieldEdit(key, false, null);
            text.AppendLine($"{field.Label}: {Encode(Value(character, field))}");
        }
        foreach (var key in character.AdditionalProperties.Keys)
        {
            var field = new ManualFieldEdit(key, true, null);
            text.AppendLine($"{field.Label}: {Encode(Value(character, field))}");
        }
        text.Append("END CHARACTER EDIT");
        return text.ToString();
    }

    public static IReadOnlyList<ManualFieldEdit> Parse(string text, string editId)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            throw new FormatException("The edit must be no larger than 256 KiB.");
        text = text.Trim().TrimStart('\uFEFF');
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            if (firstLine < 0 || !text.EndsWith("```", StringComparison.Ordinal))
                throw new FormatException("Paste the complete block or upload its .txt file.");
            text = text[(firstLine + 1)..^3].Trim();
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "BEGIN CHARACTER EDIT" ||
            lines[^1].Trim() != "END CHARACTER EDIT")
            throw new FormatException("Keep the BEGIN CHARACTER EDIT and END CHARACTER EDIT lines.");
        if (!lines[1].Trim().Equals($"Edit ID: {editId}", StringComparison.Ordinal))
            throw new FormatException("This Edit ID does not match your current session. Use the latest template.");

        var fields = new List<ManualFieldEdit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 2; i < lines.Length - 1; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            var custom = line.StartsWith('"');
            var colon = line.IndexOf(':');
            string key;
            if (custom)
            {
                var end = 1;
                for (; end < line.Length; end++)
                {
                    if (line[end] == '\\') { end++; continue; }
                    if (line[end] == '"') break;
                }
                if (end >= line.Length || !line[(end + 1)..].TrimStart().StartsWith(':'))
                    throw new FormatException($"Line {i + 1}: expected a quoted field name followed by a colon.");
                key = JsonSerializer.Deserialize<string>(line[..(end + 1)])!;
                colon = line.IndexOf(':', end + 1);
            }
            else
            {
                if (colon < 1) throw new FormatException($"Line {i + 1}: use Field: value.");
                key = line[..colon].Trim();
            }
            if (!CharacterSchema.TryNormalizeProperty(key, out _))
                throw new FormatException($"Line {i + 1}: field names must contain 1–100 characters.");
            if (Protected.Contains(Compact(key)) || custom && BuiltIns.Contains(Compact(key)))
                throw new FormatException($"Line {i + 1}: internal or reserved fields cannot be edited as custom fields.");
            if (!custom)
            {
                var builtin = BuiltIn(key);
                custom = builtin is null;
                key = builtin ?? key;
            }
            var field = new ManualFieldEdit(key, custom, null);
            if (!seen.Add(field.Id))
                throw new FormatException($"Line {i + 1}: duplicate field {field.Label}.");
            var raw = line[(colon + 1)..].Trim();
            // Blank or omitted fields leave the current value alone.
            if (raw.Length == 0) continue;
            var value = raw.Equals("[clear]", StringComparison.OrdinalIgnoreCase) ? null :
                raw.StartsWith('"') ? JsonSerializer.Deserialize<string>(raw) : raw;
            fields.Add(field with { Value = value });
        }
        return fields;
    }

    public static ManualEditResult Prepare(Character original, IReadOnlyList<ManualFieldEdit> edits)
    {
        var candidate = JsonSerializer.Deserialize<Character>(Snapshot(original))!;
        foreach (var field in edits)
        {
            if (Value(original, field) == field.Value) continue;
            if (field.Custom)
            {
                var existing = candidate.AdditionalProperties.Keys.FirstOrDefault(key =>
                    key.Equals(field.Key, StringComparison.OrdinalIgnoreCase));
                if (field.Value is null)
                {
                    if (existing is not null) candidate.AdditionalProperties.Remove(existing);
                }
                else candidate.AdditionalProperties[existing ?? field.Key] = JsonSerializer.SerializeToElement(field.Value);
                continue;
            }
            switch (field.Key)
            {
                case "name":
                    if (!CharacterSchema.TryNormalizeName(field.Value, out var name))
                        return new(null, [], "Full Name must contain 1–100 characters and cannot begin with character:.");
                    candidate.Name = name;
                    break;
                case "aliases":
                    candidate.Aliases = field.Value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
                    break;
                case "age": candidate.Age = field.Value; break;
                case "gender": candidate.Gender = field.Value; break;
                case "region": candidate.Region = field.Value; break;
                case "occupation": candidate.Occupation = field.Value; break;
                case "reference": candidate.CharacterReference.Value = field.Value; break;
                case "reference-kind": candidate.CharacterReference.Kind = field.Value ?? ""; break;
                case "reference-format": candidate.CharacterReference.Format = field.Value ?? ""; break;
            }
        }
        if (candidate.Name != original.Name)
        {
            candidate.Aliases.RemoveAll(alias => alias.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase));
            if (!edits.Any(field => !field.Custom && field.Key == "aliases" && field.Value != Value(original, field)) &&
                CharacterSchema.TryNormalizeName(original.Name, out _) &&
                !candidate.Aliases.Contains(original.Name, StringComparer.OrdinalIgnoreCase))
                candidate.Aliases.Add(original.Name);
        }
        var all = BuiltIns.Select(key => new ManualFieldEdit(key, false, null))
            .Concat(original.AdditionalProperties.Keys.Concat(candidate.AdditionalProperties.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(key => new ManualFieldEdit(key, true, null)));
        var changes = all.Where(field => Value(original, field) != Value(candidate, field))
            .Select(field => $"{field.Label}: {Display(Value(original, field))} → {Display(Value(candidate, field))}").ToArray();
        return new(candidate, changes, null);
    }

    private static string Display(string? value) => value is null ? "(empty)" : JsonSerializer.Serialize(value);
}