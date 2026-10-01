using System.Text.Json;
using Discord;
using Yoko.Bot.Commands;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

internal static class ManualEditChecks
{
    public static async Task RunAsync(string directory, Action<bool, string> check)
    {
        var path = Path.Combine(directory, "manual-characters.json");
        var characters = new CharacterStore(path);
        var target = (await characters.AddAsync(70, 700, "Helion Altur", 701))!;
        var other = (await characters.AddAsync(70, 700, "Existing name", 701))!;
        var selector = CharacterSchema.Selector(target.PublicId);
        await characters.SetFieldAsync(70, 700, selector, "age", "28");
        await characters.SetFieldAsync(70, 700, selector, "reference", "https://example.com/sheet?q=a:b");
        await characters.SetFieldAsync(70, 700, selector, "quote", "First line\nSecond line: \"hello\"");
        await characters.SetFieldAsync(70, 700, selector, "eye-color", "Amber");
        await characters.SetFieldAsync(70, 700, selector, "colon:key", " [clear] ");
        await characters.SetFieldAsync(70, 700, selector, "literal", "[clear]");
        await characters.SetFieldAsync(70, 700, selector, "empty", "");
        target = (await characters.GetAsync(70, 700, selector))!;
        // A legacy structured extension must survive an untouched round trip.
        target.AdditionalProperties["legacy-object"] = JsonSerializer.SerializeToElement(new { note = "unchanged", count = 3 });
        target.AdditionalProperties["legacy-number"] = JsonSerializer.SerializeToElement(42);
        target.AdditionalProperties["legacy-array"] = JsonSerializer.SerializeToElement(new[] { "a", "b" });
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(
            new Dictionary<string, Dictionary<string, UserCharacters>>
            {
                ["70"] = new() { ["700"] = new() { Characters = [target, other] } }
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        target = (await characters.GetAsync(70, 700, selector))!;
        const string id = "test-edit-id";
        var template = CharacterManualEdit.Template(target, id);
        foreach (var field in target.AdditionalProperties.Keys)
            check(template.Contains(JsonSerializer.Serialize(field) + ":"), $"Template omitted custom field {field}.");
        check(template.Contains("Full Name: Helion Altur") && template.Contains("Link: https://"), "Friendly field labels missing.");
        check(!template.Contains("ApprovedAt") && !template.Contains("PublicId"), "Metadata leaked into editable template.");
        var parsed = CharacterManualEdit.Parse(template, id);
        var roundTrip = await characters.ApplyManualEditAsync(70, 700, target, parsed, commit: true);
        check(roundTrip.Error is null && roundTrip.Changes.Count == 0, "Unchanged template produced changes.");
        check(CharacterManualEdit.Snapshot((await characters.GetAsync(70, 700, selector))!) == CharacterManualEdit.Snapshot(target),
            "Round trip changed legacy types, empty values, or metadata.");
        check(CharacterManualEdit.Parse("```text\n" + template + "\n```", id).Count == parsed.Count, "Code-fenced paste did not parse.");
        check(CharacterManualEdit.Parse("\uFEFF" + template.Replace("\n", "\r\n"), id).Count == parsed.Count, "Windows file/BOM did not parse.");

        string Document(string fields) => $"BEGIN CHARACTER EDIT\nEdit ID: {id}\n\n{fields}\nEND CHARACTER EDIT";
        void Reject(string fields, string label)
        {
            try { CharacterManualEdit.Parse(Document(fields), id); check(false, label); }
            catch (Exception exception) when (exception is FormatException or JsonException) { check(true, label); }
        }
        Reject("Age: 29\nage: 30", "Duplicate field accepted.");
        Reject("Full Name: One\nname: Two", "Duplicate alias accepted.");
        Reject("public-id: forged", "Public ID accepted.");
        Reject("Approved At: yesterday", "Approval metadata accepted.");
        Reject("\"ocRoleIndex\": 90", "Quoted role metadata accepted.");
        Reject("\"name\": hidden", "Native JSON property collision accepted.");
        Reject("Age 29", "Malformed row accepted.");
        Reject("Age: \"unterminated", "Malformed quoted value accepted.");
        Reject(new string('x', 101) + ": value", "Oversized field name accepted.");
        Reject("Quote: " + new string('x', CharacterManualEdit.MaxBytes), "Oversized block accepted.");
        try { CharacterManualEdit.Parse(template, "another-id"); check(false, "Wrong edit ID accepted."); }
        catch (FormatException) { check(true, "Wrong edit ID rejected."); }
        try { CharacterManualEdit.Parse(template.Replace("END CHARACTER EDIT", ""), id); check(false, "Truncated block accepted."); }
        catch (FormatException) { check(true, "Truncated block rejected."); }

        var edits = CharacterManualEdit.Parse(Document("Full Name: Helion Renamed\nAge: 29\nLink: [clear]\n\"eye-color\": Silver\nNew Field: A new value\n\"empty\": [clear]"), id);
        var before = await File.ReadAllTextAsync(path);
        var preview = await characters.ApplyManualEditAsync(70, 700, target, edits, commit: false);
        check(preview.Error is null && preview.Changes.Count == 7, "Preview omitted changes including rename alias.");
        check(await File.ReadAllTextAsync(path) == before, "Preview saved data.");
        var invalid = CharacterManualEdit.Parse(Document("Age: 99\nFull Name: [clear]"), id);
        check((await characters.ApplyManualEditAsync(70, 700, target, invalid, commit: true)).Error is not null, "Invalid name saved.");
        check(await File.ReadAllTextAsync(path) == before, "Invalid edit partially saved.");
        var duplicate = CharacterManualEdit.Parse(Document("Age: 99\nFull Name: Existing name"), id);
        check((await characters.ApplyManualEditAsync(70, 700, target, duplicate, commit: true)).Error is not null, "Duplicate name saved.");
        check(await File.ReadAllTextAsync(path) == before, "Duplicate name partially saved.");
        check((await characters.ApplyManualEditAsync(71, 700, target, edits, commit: true)).Error is not null, "Cross-server edit accepted.");
        check((await characters.ApplyManualEditAsync(70, 701, target, edits, commit: true)).Error is not null, "Cross-owner edit accepted.");

        var saved = await characters.ApplyManualEditAsync(70, 700, target, edits, commit: true);
        var updated = (await characters.GetAsync(70, 700, selector))!;
        check(saved.Error is null && saved.Changes.SequenceEqual(preview.Changes), "Save differed from preview.");
        check(updated.Name == "Helion Renamed" && updated.Age == "29" && updated.CharacterReference.Value is null, "Baseline changes missing.");
        check(updated.Aliases.Contains("Helion Altur"), "Rename did not retain old name as alias.");
        check(updated.AdditionalProperties["eye-color"].GetString() == "Silver" && updated.AdditionalProperties.ContainsKey("New Field") &&
            !updated.AdditionalProperties.ContainsKey("empty"), "Custom updates/addition/clear failed.");
        check(updated.AdditionalProperties["legacy-object"].ValueKind == JsonValueKind.Object &&
            updated.AdditionalProperties["legacy-number"].ValueKind == JsonValueKind.Number &&
            updated.AdditionalProperties["legacy-array"].ValueKind == JsonValueKind.Array, "Unedited structured custom fields changed.");
        check(updated.PublicId == target.PublicId && updated.ApprovedAt == target.ApprovedAt &&
            updated.ApprovedBy == target.ApprovedBy && updated.OcRoleIndex == target.OcRoleIndex, "Bulk edit changed metadata.");
        check((await characters.ApplyManualEditAsync(70, 700, target, edits, commit: true)).Error is not null, "Stale template overwrote saved changes.");
        var newerEdits = CharacterManualEdit.Parse(Document("Age: 30"), id);
        check((await characters.ApplyManualEditAsync(70, 700, updated, newerEdits, commit: false)).Error is null, "Fresh preview rejected.");
        await characters.SetFieldAsync(70, 700, selector, "occupation", "Changed during preview");
        check((await characters.ApplyManualEditAsync(70, 700, updated, newerEdits, commit: true)).Error is not null, "Post-preview change was overwritten.");
        check((await characters.GetAsync(70, 700, selector))!.Age == "29", "Conflict partially saved.");

        updated = (await characters.GetAsync(70, 700, selector))!;
        var competing = await Task.WhenAll(
            characters.ApplyManualEditAsync(70, 700, updated, CharacterManualEdit.Parse(Document("Age: 31"), id), commit: true),
            characters.ApplyManualEditAsync(70, 700, updated, CharacterManualEdit.Parse(Document("Age: 32"), id), commit: true));
        check(competing.Count(result => result.Error is null) == 1, "Concurrent confirmations both overwrote data.");
        updated = (await characters.GetAsync(70, 700, selector))!;
        await characters.DeleteAsync(70, 700, selector);
        check((await characters.ApplyManualEditAsync(70, 700, updated, newerEdits, commit: true)).Error is not null, "Deleted character recreated by old template.");

        check(CommandPermissionResolver.CharacterEditPermissions(700, 700).SequenceEqual(["character.edit.self", "character.edit.any"]),
            "Self edit permissions wrong.");
        check(CommandPermissionResolver.CharacterEditPermissions(701, 700).SequenceEqual(["character.edit.any"]), "Other-owner edit allowed self permission.");
        var command = (SlashCommandProperties)CharacterCommands.Build().Single();
        var option = command.Options.Value.Single(item => item.Name == "manual-edit");
        check(option.Options.Select(item => item.Name).SequenceEqual(["user", "character-name"]), "Manual-edit command options missing.");
    }
}