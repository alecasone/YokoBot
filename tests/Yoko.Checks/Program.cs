using System.Text.Json;
using Yoko.Bot.Commands;
using Yoko.Bot.Models;
using Yoko.Bot.Services;

var testDirectory = Path.Combine(Path.GetTempPath(), "yoko-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var characters = new CharacterStore(Path.Combine(testDirectory, "characters.json"));
var store = new SceneStore(Path.Combine(testDirectory, "scenes.json"), characters);
var a = (await characters.AddAsync(1, 10, "Aster", 10))!;
var b = (await characters.AddAsync(1, 10, "Birch", 10))!;
var c = (await characters.AddAsync(1, 20, "Cedar", 20))!;
var date = new WorldDate { Day = 1, Month = 9, Year = 2026 };

// Concurrent creations must claim capacity under the same store lock.
var batch = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.CreateAsync(1, 10, a, date, null)));
Check(batch.Count(result => result.Scene is not null) == 4, "Concurrent creates exceeded four scenes.");
Check(batch.First(r => r.Scene is not null).Scene!.Title == "Scene #1 — 01-09-2026", "Default title lacks number/date.");
Check((await store.CreateAsync(1, 10, b, date, "Birch scene")).Scene is not null, "One character's cap blocked another.");
Check((await store.GetSettingsAsync(2)).ActiveLimitPerCharacter == 4, "Default limit is wrong.");
Check((await store.GetCapacityAsync(2, 10, a.PublicId)).Active == 0, "Guild counts leaked.");

var first = batch.First(r => r.Scene is not null).Scene!;
await store.CompleteAsync(1, first.Id);
Check((await store.GetCapacityAsync(1, 10, a.PublicId)).Active == 3, "Completion did not free a slot.");
var replacement = (await store.CreateAsync(1, 10, a, date, null)).Scene!;
Check(replacement.Number == 6, "Scene numbering was reused.");
await store.DeleteAsync(1, replacement.Id);
Check((await store.GetCapacityAsync(1, 10, a.PublicId)).Active == 3, "Deletion did not free a slot.");

await characters.SetFieldAsync(1, 10, CharacterSchema.Selector(a.PublicId), "name", "Aster Renamed");
a = (await characters.GetAsync(1, 10, CharacterSchema.Selector(a.PublicId)))!;
Check((await store.GetCapacityAsync(1, 10, a.PublicId)).Active == 3, "Rename reset the count.");
Check((await store.GetActiveAsync(1)).Any(s => s.Participants.Any(p => p.Characters.Contains("Aster Renamed"))), "Scene name not updated after rename.");
await store.ChangeSlotsAsync(1, 10, 2);
Check((await store.GetCapacityAsync(1, 10, b.PublicId)).Limit == 6, "Bonus must apply to every owned character.");
Check((await store.GetCapacityAsync(1, 20, c.PublicId)).Limit == 4, "Bonus leaked to another owner.");
await store.ConfigureAsync(1, 2, "Helios", "{user}: {charactername} joined {scene} on {date}.");
await store.ChangeSlotsAsync(1, 10, 0, reset: true);
Check((await store.CreateAsync(1, 10, a, date, null)).Scene is null, "Lowering limits allowed an over-cap create.");
Check((await store.GetCapacityAsync(1, 10, a.PublicId)).Active == 3, "Lowering limits destroyed existing scenes.");

// Acceptance must check capacity again, since slots can fill after an invitation.
var sceneC = (await store.CreateAsync(1, 20, c, date, "Invite target")).Scene!;
var pending = new PendingSceneInvite { InvitationMessageId = 90, SceneId = sceneC.Id, InvitedUserId = 10,
    CharacterId = b.PublicId, CharacterName = b.Name, InvitedBy = 20 };
Check(await store.AddPendingInviteAsync(1, pending) == SceneMutationStatus.Success, "Invitation should be allowed.");
Check((await store.CreateAsync(1, 10, b, date, "Fill last slot")).Scene is not null, "Last slot unavailable.");
Check(await store.AddCharacterAsync(1, sceneC.Id, 10, b) == SceneMutationStatus.AtCapacity, "Acceptance bypassed capacity.");
Check(await store.GetPendingInviteAsync(1, 90) is not null, "Capacity failure consumed invitation.");
await store.ChangeSlotsAsync(1, 10, 1);
Check(await store.AddCharacterAsync(1, sceneC.Id, 10, b) == SceneMutationStatus.Success, "Bonus did not allow acceptance.");
Check(await store.AddCharacterAsync(1, sceneC.Id, 10, b) == SceneMutationStatus.AlreadyExists, "Duplicate acceptance altered capacity.");
await store.RemoveCharacterAsync(1, sceneC.Id, 10, b.Name);
Check((await store.GetCapacityAsync(1, 10, b.PublicId)).Active == 2, "Removing a participant did not free capacity.");
var reopened = new SceneStore(Path.Combine(testDirectory, "scenes.json"), characters);
Check((await reopened.GetSettingsAsync(1)).ReplyName == "Helios", "Settings did not persist.");

// Migrate pre-ID scene records, including names that became aliases after a rename.
var legacyPath = Path.Combine(testDirectory, "legacy-scenes.json");
await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new Dictionary<ulong, SceneGuildData>
{
    [1] = new() { Scenes = [new() { Title = "Legacy", WorldDate = date,
        Participants = [new() { UserId = 10, Characters = ["Aster"] }] }] }
}, jsonOptions));
var legacy = new SceneStore(legacyPath, characters);
Check((await legacy.GetCapacityAsync(1, 10, a.PublicId)).Active == 1, "Legacy alias migration lost capacity.");
var legacyScene = (await legacy.GetActiveAsync(1)).Single();
await legacy.ConfigureAsync(1, 8, "Helios", null);
await legacy.DeleteAsync(1, legacyScene.Id);
Check((await legacy.GetSettingsAsync(1)).ActiveLimitPerCharacter == 8, "Deleting last scene erased settings.");

Check(SceneDialogue.IsReply("Accept, Helios.", "accept", "Helios"), "Custom reply name not recognized.");
Check(SceneDialogue.IsReply("ACCEPT", "accept", "Helios"), "Plain accept not recognized.");
Check(!SceneDialogue.IsReply("Accept, Somebody.", "accept", "Helios"), "Wrong reply name recognized.");
Check(SceneDialogue.Render("{user} **{charactername}** / {scene}", "<@10>", "Aster {scene}", "Soup", "today") ==
      "<@10> **Aster {scene}** / Soup", "Template substitution interpreted character content.");
Check(!SceneDialogue.IsValidTemplate(string.Concat(Enumerable.Repeat("{charactername}", 25))), "Repeated placeholder overflow was accepted.");

// Social inverses appear only after consent and never cause biological inference.
foreach (var type in RelationshipCatalog.Definitions)
    Check(RelationshipCatalog.Get(type.InverseId)?.InverseId == type.Id, $"Invalid inverse: {type.Id}");
Check(RelationshipCatalog.Resolve("married")?.Id == "romantic-spouse", "Married alias missing.");
var relationships = new RelationshipStore(Path.Combine(testDirectory, "relationships.json"));
var request = new PendingRelationshipRequest { SourceCharacterId = a.PublicId, SourceOwnerId = 10,
    TargetCharacterId = c.PublicId, TargetOwnerId = 20, TypeId = "romantic-spouse" };
await relationships.AddPendingAsync(1, request);
Check((await relationships.GetDirectAsync(1)).Count == 0, "Pending social relationship was published as approved.");
Check((await relationships.ApproveAsync(1, request.Id, 10)).Status == RelationshipMutationStatus.NotAuthorized, "Wrong owner could approve.");
await relationships.ApproveAsync(1, request.Id, 20);
var inference = new RelationshipInferenceEngine();
var graph = inference.Build(await relationships.GetDirectAsync(1));
Check(graph.Count == 2 && graph.All(e => !e.IsInferred && e.TypeId == "romantic-spouse"), "Social tie inferred biological facts.");
var mentor = inference.Build([new() { SourceCharacterId = a.PublicId, TargetCharacterId = b.PublicId, TypeId = "societal-mentor" }]);
Check(mentor.Any(e => e.SourceCharacterId == b.PublicId && e.TypeId == "societal-student"), "Asymmetric societal inverse missing.");
var siteSettings = new SiteSettingsStore(Path.Combine(testDirectory, "site-settings.json"));
await siteSettings.SetBrandingAsync(1, new SiteBranding { Name = "Helios", LogoLetter = "H" });
var identities = new PublicIdentityStore(Path.Combine(testDirectory, "identities.json"));
var exporter = new PublicSiteExporter(characters, relationships, inference, siteSettings, identities,
    (_, owner) => owner == 10 ? "Example Writer" : "Other Writer");
using var exported = JsonDocument.Parse(await exporter.BuildJsonAsync(1));
Check(exported.RootElement.GetProperty("branding").GetProperty("name").GetString() == "Helios", "Branding not exported.");
Check(exported.RootElement.GetProperty("relationshipTypes").EnumerateArray().Any(t => t.GetProperty("category").GetString() == "Social"), "Social catalog missing from export.");
Check(!exported.RootElement.GetRawText().Contains("sourceOwnerId"), "Export leaked private owner data.");

// Build the actual slash-command definitions, exercising Discord.Net's validation.
Check(SceneTrackerCommands.Build() is not null, "Scene commands did not build.");
Check(RelationshipCommands.Build() is not null, "Relationship commands did not build.");
Check(SiteAdminCommands.Build() is not null, "Site commands did not build.");
Check(CharacterCommands.Build().Length == 1, "Character commands did not build.");
Check(PermissionCatalog.Matches("scenetracker.*", "scenetracker.slots"), "Moderator wildcard does not cover slot grants.");

// Opt-out is enforced in exported data, across every owned character, without erasing the public name.
var publicOwner = exported.RootElement.GetProperty("characters")[0].GetProperty("owner");
Check(publicOwner.GetProperty("discordId").ValueKind == JsonValueKind.String, "Owner snowflake must be a JSON string.");
await identities.SetPrivacyAsync(1, 10, true, "Example Writer");
using var privateExport = JsonDocument.Parse(await exporter.BuildJsonAsync(1));
var myRecords = privateExport.RootElement.GetProperty("characters").EnumerateArray()
    .Where(record => record.GetProperty("owner").GetProperty("displayName").GetString() == "Example Writer").ToArray();
Check(myRecords.Length == 2 && myRecords.All(record => !record.GetProperty("owner").TryGetProperty("discordId", out _)), "Hidden owner ID leaked or a character lost attribution.");
Check(privateExport.RootElement.GetProperty("characters").EnumerateArray().Any(record => record.GetProperty("owner").TryGetProperty("discordId", out _)), "Hiding one owner affected another.");
Check((await new PublicIdentityStore(Path.Combine(testDirectory, "identities.json")).GetOrCreateAsync(1, 10)).HideDiscordId, "Privacy did not survive restart.");
Check(!(await identities.GetOrCreateAsync(2, 10)).HideDiscordId, "Privacy leaked between servers.");
await identities.GetOwnersAsync(1, new Dictionary<ulong, string?> { [10] = "Renamed Writer" });
Check((await identities.GetOrCreateAsync(1, 10)).HideDiscordId, "A display-name refresh erased opt-out.");

// Never send requests to GitHub: the injected HTTP handler records locally and controls the race.
await identities.SetPrivacyAsync(1, 10, false, "Example Writer");
await siteSettings.ConfigureAsync(1, "test-owner", "test-repo", "pages", "docs/data/characters.json", "https://example.test/");
var transport = new RecordingGitHubHandler();
using var localHttp = new HttpClient(transport);
var publisher = new SitePublicationService(siteSettings, exporter, new GitHubContentsClient(localHttp, () => "fake-test-token"));
var olderPublish = publisher.PublishNowAsync(1);
await transport.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
var optOut = publisher.ChangeOwnerPrivacyAsync(1, 10, true, "Example Writer", identities);
Check(!optOut.IsCompleted, "Privacy update raced ahead of an older publish.");
transport.ReleaseFirstWrite.SetResult();
await olderPublish;
var optedOut = await optOut;
Check(optedOut.Saved && optedOut.Publication.Success && transport.Snapshots.Count == 2, "Privacy did not publish while autopublish was off.");
using var lastSnapshot = JsonDocument.Parse(transport.Snapshots.Last());
Check(lastSnapshot.RootElement.GetProperty("characters").EnumerateArray()
    .Where(record => record.GetProperty("owner").GetProperty("displayName").GetString() == "Example Writer")
    .All(record => !record.GetProperty("owner").TryGetProperty("discordId", out _)), "Last snapshot restored a hidden ID.");
Check(!(await siteSettings.GetAsync(1)).AutoPublish, "Privacy command changed routine autopublish policy.");
var offlinePublisher = new SitePublicationService(siteSettings, exporter, new GitHubContentsClient(tokenProvider: () => null));
var offlineResult = await offlinePublisher.ChangeOwnerPrivacyAsync(1, 10, false, "Example Writer", identities);
Check(offlineResult.Saved && !offlineResult.Publication.Success && (await siteSettings.GetAsync(1)).PendingChanges, "Offline publication must report saved but pending.");
await characters.AddAsync(4, 111111111111111111, "Exact Snowflake", 1);
using var exactSnapshot = JsonDocument.Parse(await exporter.BuildJsonAsync(4));
Check(exactSnapshot.RootElement.GetProperty("characters")[0].GetProperty("owner").GetProperty("discordId").GetString() == "111111111111111111", "Snowflake precision lost.");

var permissionStore = new PermissionStore(Path.Combine(testDirectory, "permissions.json"));
var permissionService = new PermissionService(permissionStore);
var unverifiedMember = System.Reflection.DispatchProxy.Create<Discord.IUser, TestMember>();
Check(await permissionService.HasAsync(1, unverifiedMember, "character.website-privacy"), "Privacy not available to an unverified member.");
Check(!await permissionService.HasAsync(1, unverifiedMember, "character.purge.server"), "Ordinary member received purge access.");
await permissionStore.GrantUserAsync(1, "character.delete.any", unverifiedMember.Id);
Check(!await permissionService.HasAsync(1, unverifiedMember, "character.purge.user"), "Ordinary delete permission implicitly granted purge.");
await permissionStore.GrantUserAsync(1, "character.purge.user", unverifiedMember.Id);
Check(await permissionService.HasAsync(1, unverifiedMember, "character.purge.user") &&
    !await permissionService.HasAsync(1, unverifiedMember, "character.purge.server"), "User purge permission leaked into server purge.");

// Typed confirmation state machine is actor/channel/server-bound, expiring, and single-use.
var now = DateTimeOffset.UtcNow;
var confirm = new CharacterPurgeConfirmation(50, 99, 77, 101, [a.PublicId], now);
Check(confirm.Reply(50, 98, 77, confirm.TargetText, now, true) == PurgeConfirmationStep.WrongActor, "Another member could confirm.");
Check(confirm.Reply(50, 99, 76, confirm.TargetText, now, true) == PurgeConfirmationStep.WrongActor, "Another channel could confirm.");
Check(confirm.Reply(51, 99, 77, confirm.TargetText, now, true) == PurgeConfirmationStep.WrongActor, "Another server could confirm.");
Check(confirm.Reply(50, 99, 77, confirm.FinalText, now, true) == PurgeConfirmationStep.Mismatch, "Final code skipped scope confirmation.");
Check(confirm.Reply(50, 99, 77, confirm.TargetText, now, true) == PurgeConfirmationStep.TargetConfirmed, "First confirmation failed.");
Check(confirm.Reply(50, 99, 77, "CONFIRM BAD", now, true) == PurgeConfirmationStep.Mismatch, "Wrong final code accepted.");
Check(confirm.Reply(50, 99, 77, confirm.FinalText, now, true) == PurgeConfirmationStep.Execute, "Second confirmation failed.");
Check(confirm.Reply(50, 99, 77, confirm.FinalText, now, true) == PurgeConfirmationStep.Expired, "Purge confirmation was replayed.");
var expired = new CharacterPurgeConfirmation(50, 99, 77, null, [a.PublicId], now);
Check(expired.Reply(50, 99, 77, expired.TargetText, now.AddMinutes(5), true) == PurgeConfirmationStep.Expired, "Old confirmation did not expire.");
var revoked = new CharacterPurgeConfirmation(50, 99, 77, null, [a.PublicId], now);
Check(revoked.Reply(50, 99, 77, revoked.TargetText, now, false) == PurgeConfirmationStep.Expired, "Revoked permission still allowed deletion.");
var cancelled = new CharacterPurgeConfirmation(50, 99, 77, null, [a.PublicId], now);
Check(cancelled.Reply(50, 99, 77, "cancel", now, true) == PurgeConfirmationStep.Cancelled, "Cancel did not stop deletion.");

var purgeA = (await characters.AddAsync(50, 101, "Purge A", 99))!;
var purgeB = (await characters.AddAsync(50, 101, "Purge B", 99))!;
var keep = (await characters.AddAsync(50, 102, "Keep", 99))!;
await characters.AddAsync(51, 101, "Other server", 99);
var scopedIds = new[] { purgeA.PublicId, purgeB.PublicId };
var rolesSynced = new List<ulong>();
var sitesQueued = new List<ulong>();
var purgeService = new CharacterPurgeService(characters, relationships, store,
    (_, id) => { rolesSynced.Add(id); return Task.FromResult(new CharacterRoleSyncResult(true, null, 0, 7, null)); },
    guild => { sitesQueued.Add(guild); return Task.CompletedTask; });
var addedDuringConfirmation = (await characters.AddAsync(50, 101, "New arrival", 99))!;
var changedRoster = await purgeService.PurgeAsync(50, 101, scopedIds);
Check(changedRoster.RosterChanged && await characters.GetCharacterCountAsync(50, 101) == 3, "Changed roster was purged.");
Check(sitesQueued.Count == 0 && rolesSynced.Count == 0, "Failed confirmation changed roles or published.");
var solo = (await store.CreateAsync(50, 101, purgeA, date, "Solo history")).Scene!;
await store.CompleteAsync(50, solo.Id);
var shared = (await store.CreateAsync(50, 101, purgeB, date, "Shared scene")).Scene!;
await store.AddCharacterAsync(50, shared.Id, 102, keep);
await store.AddPendingInviteAsync(50, new() { InvitationMessageId = 1001, SceneId = shared.Id, InvitedUserId = 101, CharacterId = purgeA.PublicId, CharacterName = purgeA.Name });
var relPurge = new PendingRelationshipRequest { SourceOwnerId = 101, SourceCharacterId = purgeA.PublicId, TargetOwnerId = 102, TargetCharacterId = keep.PublicId, TypeId = "social-friend" };
await relationships.AddPendingAsync(50, relPurge);
await relationships.ApproveAsync(50, relPurge.Id, 102);
await relationships.AddPendingAsync(50, new() { SourceOwnerId = 101, SourceCharacterId = purgeB.PublicId, TargetOwnerId = 102, TargetCharacterId = keep.PublicId, TypeId = "social-rival" });
await store.ConfigureAsync(50, 8, "Helios", null);
await identities.SetPrivacyAsync(50, 101, true, "Keep privacy preference");
var purgedUser = await purgeService.PurgeAsync(50, 101, [..scopedIds, addedDuringConfirmation.PublicId]);
Check(purgedUser.Deleted.Count == 3 && purgedUser.Warnings.Count == 0, "User purge failed.");
Check(await characters.GetCharacterCountAsync(50, 101) == 0 && await characters.GetCharacterCountAsync(50, 102) == 1, "User purge deleted the wrong owner's records.");
Check(await characters.GetCharacterCountAsync(51, 101) == 1, "Purge crossed servers.");
Check((await relationships.GetDirectAsync(50)).Count == 0 && (await relationships.GetRequestsForUserAsync(50, 102)).Count == 0, "Relationship cleanup incomplete.");
Check(await store.GetAsync(50, solo.Id) is null, "Empty completed scene was retained.");
Check((await store.GetAsync(50, shared.Id))!.Participants.Single().Characters.Single() == "Keep", "Shared scene was destroyed or retained purged characters.");
Check(await store.GetPendingInviteAsync(50, 1001) is null, "Purge retained scene invitation.");
Check((await identities.GetOrCreateAsync(50, 101)).HideDiscordId, "Purge erased privacy preferences.");
Check(rolesSynced.SequenceEqual([101UL]) && sitesQueued.SequenceEqual([50UL]), "Roles/publishing did not reconcile exactly once.");
var failureService = new CharacterPurgeService(characters, relationships, store,
    (_, _) => Task.FromResult(new CharacterRoleSyncResult(false, null, 0, 7, "Missing Permissions")),
    guild => { sitesQueued.Add(guild); return Task.CompletedTask; });
var serverPurge = await failureService.PurgeAsync(50, null, [keep.PublicId]);
Check(serverPurge.Deleted.Count == 1 && serverPurge.Warnings.Count == 1 && sitesQueued.Count == 2, "Role failure hid the purge result or prevented publication.");
Check((await characters.GetAllAsync(50)).Count == 0 && (await store.GetAllAsync(50)).Count == 0, "Server purge left character/scene data.");
Check((await store.GetSettingsAsync(50)).ActiveLimitPerCharacter == 8, "Purge erased server configuration.");

Console.WriteLine($"Passed {assertions} checks. Isolated fixture files: {testDirectory}");

public class TestMember : System.Reflection.DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => method?.Name == "get_Id" ? 999UL : null;
}

sealed class RecordingGitHubHandler : HttpMessageHandler
{
    public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Snapshots { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"sha\":\"old-test-sha\"}") };
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        Snapshots.Add(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("content").GetString()!)));
        if (Snapshots.Count == 1) { FirstWriteStarted.SetResult(); await ReleaseFirstWrite.Task.WaitAsync(cancellationToken); }
        return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"commit\":{\"sha\":\"new-test-sha\"}}") };
    }
}
