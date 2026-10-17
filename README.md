# Yoko Discord Bot

A .NET 8 Discord bot using Discord.Net for character management, verification, roleplay scenes, auto moderation, and an ominous `/shutdown` Ouija-board sequence.

## Command permissions

Yoko uses an internal, per-server PEX-style permission system stored in `data/permissions.json`. Discord server Administrators always bypass the file. Other members receive permissions through stable Discord role IDs or optional direct user grants. Exact names such as `character.approve`, section wildcards such as `character.*`, and the global `*` wildcard are supported.

The first time a server checks permissions, Yoko seeds these role assignments:

- **Admin** (`1541979162242195466`) receives `*`.
- **Moderator** (`1541979611754135643`) receives character approval/edit/view/delete access, verification, auto-moderation viewing and approvals, member-alert viewing, reusable reminder activation, the verification recheck, every scene-tracker permission (including limits and slot grants), relationship access, and permission viewing.
- **Verified** (`1542018931894521887`) receives `/ping`, self-scoped character edit/view/delete, scene creation/view/history, management of scenes in which they participate, relationship access, and reusable reminder activation.

Use `/permissions list` to see every recognized permission together with the roles and direct users that currently receive it, including inherited wildcard grants. `/permissions view permission` focuses on one permission, and `/permissions role role` inspects a role. Members with `permissions.manage` can use `/permissions grant`, `/permissions revoke`, `/permissions grant-user`, and `/permissions revoke-user`. Removing a seeded grant is persistent; restarting Yoko does not add it back. Keep at least one Discord Administrator available because that bypass cannot be removed from the JSON file.

Discord only controls visibility at the top-level slash-command name, not separately for `/character approve` and `/character view`. Yoko therefore leaves command roots visible and privately rejects an unauthorized subcommand. Handlers and autocomplete both recheck internal permissions. Character permissions distinguish `.self` from `.any`, while scene management distinguishes participant-scoped `.own` from moderator-style `.any`.

### Permission reference

| Permission | Affected command or action |
| --- | --- |
| `ping` | `/ping` |
| `bot.shutdown` | `/shutdown` |
| `character.approve` | `/character approve` for any member |
| `character.edit.self` | `/character edit` and `/character remove-field` when the selected user is yourself |
| `character.edit.any` | `/character edit` and `/character remove-field` for any member; also satisfies the self check |
| `character.view.self` | `/character view` when the selected user is yourself |
| `character.view.any` | `/character view` for any member; also satisfies the self check |
| `character.delete.self` | `/character delete` when the selected user is yourself |
| `character.delete.any` | `/character delete` for any member; also satisfies the self check |
| `character.purge.user` | `/character purge-user`: permanently delete one member's entire character roster in this server |
| `character.purge.server` | `/character purge-server`: permanently delete every character in this server |
| `character.website-privacy` | `/character anonymize-website-discord-id`: always available to everyone for their own account; cannot be revoked |
| `character.configure.properties` | `/charadmin properties view`, `/charadmin properties add`, and `/charadmin properties remove` |
| `character.configure.autofill` | `/charadmin autofill add` and `/charadmin autofill remove` |
| `character.configure.roles` | `/charadmin roles add` and `/charadmin roles remove` |
| `character.configure.approval-messages` | `/charadmin approvemessage add` and `/charadmin approvemessage delete` |
| `verification.verify` | `/verify` |
| `verification.configure.roles` | `/verifyadmin role add`, `/verifyadmin role edit`, and `/verifyadmin role delete` |
| `verification.configure.success-message` | `/verifyadmin successmessage` |
| `automod.add` | `/automod add` |
| `automod.delete` | `/automod delete` |
| `automod.view` | `/automod view` |
| `automod.approve` | Replying `Confirm, Yoko.` or `Cancel, Yoko.` to a queued moderation approval message |
| `alerts.view` | `/alertadmin view` |
| `alerts.configure.leave` | `/alertadmin leave add`, `/alertadmin leave edit`, and `/alertadmin leave delete` |
| `alerts.configure.new-account` | `/alertadmin newaccount set` and `/alertadmin newaccount delete` |
| `automessage.manage` | Every `/automessage` command, including the interactive message editor |
| `reminder.manage` | `/reminder`: set up, edit, select, view, cancel, or delete saved reminders; also permits `/remind` |
| `reminder.activate` | `/remind`: start the server's selected reminder with no fields |
| `debug.recheck-verified` | `/debug recheck-verified` |
| `overworld.worlddate` | `/overworld worlddate` |
| `scenetracker.create` | `/scenetracker create` |
| `scenetracker.view` | `/scenetracker view` |
| `scenetracker.history` | `/scenetracker history` |
| `scenetracker.manage.own` | `/scenetracker invite`, `/scenetracker complete`, `/scenetracker delete`, and both `/scenetracker edit` actions, but only for scenes in which the command user participates |
| `scenetracker.manage.any` | The same scene-management commands for any scene, without needing to participate |
| `scenetracker.configure` | `/scenetracker settings`: default cap, reply name, and acceptance message |
| `scenetracker.slots` | `/scenetracker slots add`, `remove`, `reset`, and `view`: per-user bonus slots |
| `permissions.view` | `/permissions list`, `/permissions view`, and `/permissions role` |
| `permissions.manage` | `/permissions grant`, `/permissions revoke`, `/permissions grant-user`, and `/permissions revoke-user`; also permits all permission-viewing commands |
| `site.view` | `/siteadmin status` |
| `site.publish` | `/siteadmin publish`; also permits `/siteadmin status` |
| `site.configure` | `/siteadmin setup`, `/siteadmin autopublish`, and `/siteadmin branding`; also permits publishing and status |
| `relationship.request` | `/relationship request` for one of your characters |
| `relationship.respond` | `/relationship requests`, `/relationship approve`, `/relationship decline`, and replying `Accept` or `Decline` to your incoming request |
| `relationship.remove` | `/relationship remove` for a direct relationship involving one of your characters |
| `relationship.view` | `/relationship view` for all approved categories and inferred biological relationships |

Wildcard grants affect every matching permission in this table. For example, `character.*` grants every character and charadmin permission, while `character.configure.*` grants only the four charadmin configuration sections. The global `*` grants every current and future permission.

## Public character archive

The GitHub Pages scaffold in `docs/` is a searchable master directory built from approved character data and public owner attribution. Character cards, character details, and the relationship inspector show **By {display name}** and, by default, a button to copy the owner's Discord user ID. Members can hide their ID with the self-service command below; their display name remains public. Verification state, roles, moderation data, and private administrative metadata are excluded. Follow `GITHUB_PAGES_SETUP.md` to preview it locally and publish it from the dedicated `pages` branch and `/docs` folder. Keeping bot-generated data commits off `main` prevents them from interfering with normal source-code work.

Characters retain stable random `publicId` values, so renames do not break public links or relationships. Renaming a character automatically preserves the previous name as an alias; `/character edit` can also set the `aliases` field from a comma-separated list, and `/character remove-field` can clear it. The ignored local file `data/public-identities.json` keeps existing account mappings, cached display names, and per-server ID-privacy preferences. That internal file is never exported wholesale; only the owner's display name and optional Discord ID are included on public character records. Back up this file along with the other live data so opt-outs survive a host move.

`/siteadmin setup` configures the repository, branch, JSON path, and public URL. `/siteadmin publish` publishes one complete sanitized snapshot immediately, `/siteadmin autopublish` controls automatic publication, and `/siteadmin status` reports pending changes, token availability, the last attempt, the last successful commit, and any error. Character approval, approval-fillout values, edits, removed fields, aliases, renames, confirmed deletion (including bulk purges), approved relationships, removed relationships, and observed owner display-name changes all mark the snapshot dirty. Automatic publishing waits 20 seconds after the most recent change so a burst of edits becomes one commit.

### Hide your Discord ID on the website

Everyone, including members without a configured role, can run `/character anonymize-website-discord-id`. The optional `enabled` argument defaults to `true`; use `enabled:false` to show the ID again. There is no target-user option: this setting applies only to the caller, to all their current and future characters **in the current server**. It persists across restarts and character deletion. It cannot be disabled through `/permissions`.

An opt-out removes `owner.discordId` from exported JSON, not merely the copy button. **By {display name}** stays visible. The bot attempts to publish immediately, even if normal auto-publishing is off. If setup, credentials, or GitHub are unavailable, the command reports that the preference is saved but the live site has not updated; staff can resolve the issue and run `/siteadmin publish`. A successful commit still needs GitHub Pages to finish deploying. This publishes the complete current directory snapshot and does not enable routine auto-publishing.

This is ID hiding, not full anonymity. Previously published IDs can remain in Git history, caches, or copies; the command does not erase those. It also does not scrub IDs someone manually wrote into a character property or reference link. Display names use the server nickname when available, falling back to a cached account name; older records without a known name show `Unknown member` until one is available.

## Character workflow

Character data is stored locally in `data/characters.json`, keyed first by Discord server ID and then by the owner's Discord user ID. Characters with the same owner are therefore isolated between servers. The file is excluded from Git because it contains live server data. Back it up separately before deploying or moving the bot.

Character commands (subject to the permission nodes above):

- `/character approve user full-name [age] [gender] [region]` creates the character using its **Full Name**. Character names are limited to **100 characters**, matching Discord's autocomplete limit; biographies and descriptions belong in character properties instead. Age, gender, and region can be supplied immediately; a private prompt asks only for remaining baseline fields. Reply `skip` to leave a value empty, `stop`/`end` to save and exit early, or `Abort` (case-insensitive) at any fillout prompt to cancel approval and remove the newly created character. Aborting cleans up relationships and scenes, reconciles OC roles, and queues a website update. Approval messages are sent only when fillout completes or is saved with `stop`/`end`. The bot immediately deletes each channel reply and updates the ephemeral prompt. Discord does not permit truly empty messages.
- `/character manual-edit user character-name` DMs a prefilled text template containing all current baseline and custom character fields. Edit it in Notepad, paste the complete block back into that DM (or upload its UTF-8 `.txt` file), review the preview, then reply `Confirm` to save all changes together or `Abort` to discard them.
- `/character edit user character-name field value` changes a field. Baseline names are `name`, `age`, `gender`, `region`, `occupation`, `reference`, `reference-kind`, and `reference-format`. Any other name creates a flexible custom property.
- `/character remove-field user character-name field` clears a baseline value or deletes a custom property.
- `/character view user character-name` previews the current record privately.
- `/character delete user character-name` starts permanent deletion. The command user must then type `confirm CharacterName`; the bot deletes that reply and removes the entire character record, its relationships, pending invitations, and scene participation. `cancel` stops deletion.
- `/character purge-user user` starts double-confirmed deletion of all characters belonging to that user in this server.
- `/character purge-server` starts double-confirmed deletion of every character in this server, without affecting other servers.
- `/character anonymize-website-discord-id [enabled]` controls your own public Discord ID; see the privacy section above.

### Bulk deletion: the nuclear options

Bulk deletion requires the separate `character.purge.user` or `character.purge.server` permission. The default Admin `*` grant and Discord Administrator bypass allow both. Moderators do **not** receive these by default, and `character.delete.any` does not grant them. Staff can explicitly grant, for example, `/permissions grant role:@Moderator permission:character.purge.user` without also permitting a server-wide wipe. A `character.*` wildcard includes both, so grant it carefully.

The private prompt previews the scope and count. In the same channel, type the exact requested `DELETE USER {user ID}` or `DELETE SERVER {server ID}` text. Then type the separately generated `CONFIRM {code}`. Only that command user can confirm; permission is rechecked on replies, the process expires after five minutes, and `cancel` stops it. Replies are deleted when the bot has permission. If characters are added or removed while confirming, the operation refuses to delete anything and asks you to review a fresh scope.

Successful cleanup removes the character records, approved and pending relationships, scene invitations, and scene participation, including completed history. A scene left without any participants is deleted; scenes with other characters stay. OC roles are reconciled once per affected owner and a website update is queued (manual `/siteadmin publish` is still needed when auto-publishing is off). Any cleanup or role-permission failure is reported for staff to investigate; the operation is not a transaction across all files and Discord.

**There is no undo or automatic backup.** Back up the ignored `data/` files before using either command. Purges do not delete Discord chat messages, other servers, user privacy preferences, server configuration, or historical GitHub snapshots/copies. Starred default roles remain; the sequential OC roles are the ones reconciled.

The server-wide default character structure is managed with `/charadmin properties view`, `/charadmin properties add`, and `/charadmin properties remove`. Adding a property makes it appear in approval fillouts and character views for everyone. Removing a default does not destroy character-specific values already stored under that property.

Suggested approval values are managed with `/charadmin autofill add field value` and `/charadmin autofill remove field value`. Adding a suggestion for an unknown field also adds that field to the default structure. Suggestions appear in the approval command for its built-in quick fields and inside the private fillout prompt for dynamic fields. Settings are isolated by server and stored in `data/character-settings.json`.

Character approval roles are configured with `/charadmin roles add` and `/charadmin roles remove`. Each command opens a private prompt and deletes the administrator's reply. The **add** reply replaces the complete add configuration: prefix always-added roles with `*`, then mention the numbered OC roles left-to-right in their exact intended order—for example, `* @Member @1st-OC @2nd-OC @3rd-OC`. The unstarred role count becomes the per-user character cap. Approving character N ensures every starred default is present, grants the first N sequential roles, and stores `ocRoleIndex: N` on that character.

The **remove** reply replaces the list of fixed roles removed whenever a character is approved, such as `@No OC`; it does not edit or delete roles from the numbered ladder. Reply `none` in either setup to clear that configuration. Deleting a character removes the now-unused numbered role and compacts every later character index and role. Starred defaults remain untouched. The configuration is reconciled again when Yoko starts. Yoko needs **Manage Roles**, and its highest role must be above every configured role.

Use `/charadmin approvemessage add` to configure messages sent when a character is approved. The private wizard first asks for a destination: reply `dm`, `here` to dynamically use whichever channel `/character approve` is later invoked in, or mention a fixed text channel such as `#general`. Its next reply is stored verbatim as the message template, including Markdown and emoji. `{user}` becomes the character owner's mention and `{charactername}` becomes the approved character's name. After saving, the wizard asks whether to add another message and repeats the destination/template flow when the answer is `yes`.

Each add-wizard run appends messages to the existing list. `/charadmin approvemessage delete` displays a numbered destination/template summary and accepts comma-separated selections such as `1`, `1,3`, or `1,4,5`. Reply `clear` or `none` at an add-wizard destination prompt to remove every configured approval message. A delivery failure does not undo character approval; the private approval response reports how many configured messages succeeded or failed.

After selecting a user, Discord autocompletes that user's available character names. Edit/remove commands also autocomplete the baseline fields and any custom fields already stored on the selected character.

Character choices use the character's short internal selector behind the displayed name. This keeps rename-safe command selection and allows an old, oversized name to be selected, renamed, or deleted without violating Discord's 100-character autocomplete limit. When deleting such a legacy record, Yoko asks for its internal ID instead of repeating the oversized name in the confirmation message.

Other Discord-facing character configuration follows the same boundary: property names and autofill suggestions are limited to **100 characters** because they appear in autocomplete. Approval-message templates are limited to **1,800 characters**, leaving room for `{user}` and `{charactername}` to expand beneath Discord's 2,000-character message limit. Long property values remain storable; character views are safely shortened at Discord's message boundary instead of corrupting the stored value.

Every new character reference defaults to `link/sheet`; set its URL with the `reference` field. Region currently accepts text, with a dedicated validation point ready for the future region catalog.

### Manual editing in DMs

Start `/character manual-edit user:@Member character-name:Helion Altur` in the character's server. The existing `character.edit.self` / `character.edit.any` permissions apply, and permissions are checked again for DM submissions and confirmation.

The bot sends both a `.txt` template and, when short enough, a copyable code block. Every saved custom field is included, including fields outside the default approval questions. All configured server fields also appear when unfilled, with blank values ready to complete. Full Name and Link map to the existing name and reference fields. Internal IDs, approval metadata, and OC role indexes are not editable.

```text
BEGIN CHARACTER EDIT
Edit ID: (keep the generated value)

Full Name: Helion Altur
Aliases: Helion, Hel
Age: 28
Gender: Male
Region: Northern Reach
Occupation: Knight
Link: https://example.com/helion
Reference Kind: sheet
Reference Format: link
"eye-color": Amber
"hair-colour": Black
"quote": "First line\nSecond line"

END CHARACTER EDIT
```

- Edit values, or add a custom field as `Field Name: value`. Quoted field names identify exact custom keys; retain their spelling.
- Omitted or blank fields remain unchanged. `[clear]` empties a baseline field or removes a custom field; Full Name cannot be cleared. Use `"[clear]"` to store those words literally.
- Values may contain colons. For multiline text, use a JSON-quoted value with `\n` escapes, as generated by the template. Quoted values use JSON escaping for quotes and backslashes.
- Send the complete block in one DM, optionally inside a code fence. For long profiles, attach one UTF-8 `.txt` file by itself, up to 256 KiB.
- The bot previews every change before saving. Reply `Confirm` or `Abort` (case-insensitive), or submit a revised block for another preview. A rename normally retains the previous name as an alias; that addition appears in the preview.
- The edit ID stays attached to the original character even when renaming it. Duplicate fields, invalid names, metadata edits, duplicate character names, and malformed input are rejected without partial saves.
- Only one manual-edit session is active per editor across servers. A new command replaces the previous session. Sessions expire after one hour or a bot restart; request a fresh template afterward.
- If any character data changes after the template is generated, confirmation refuses to overwrite it. Start a new manual edit with the latest values.
- Successful confirmation queues the usual website update. DM privacy settings must permit the bot's messages.

## Relationships: family, social, romance, and roles

Approved relationships are stored per server in the ignored local file `data/relationships.json`. Records use stable character `publicId` values, so character renames do not break them. A direct record stores one perspective and Yoko automatically supplies the inverse perspective—for example, biological parent ↔ biological child. The sanitized Pages snapshot exports approved and inferred connections between public character IDs for the interactive relationship atlas. Pending requests and internal relationship IDs remain local. Relationship edges use character IDs, while character records carry the public owner attribution described above.

The initial biological catalog contains:

- biological parent ↔ child;
- biological sibling, full sibling, half sibling, and twin;
- biological grandparent ↔ grandchild and great-grandparent ↔ great-grandchild;
- biological aunt/uncle/pibling ↔ niece/nephew/nibling;
- biological cousin; and
- inferred biological ancestor ↔ descendant.

Autocomplete recognizes neutral labels and aliases such as mother, father, son, daughter, brother, sister, aunt, uncle, niece, and nephew. When the selected pair already has a relationship implied by the family graph, that relationship is ranked first and marked **inferred from family graph**.

The consent-based catalog also includes:

| Category | Available relationships |
| --- | --- |
| Adoptive | Parent ↔ child; sibling |
| Social | Friend, best friend, acquaintance, confidant, ally, rival, enemy |
| Romantic | Dating, engaged, spouse (search `married`), former partner |
| Societal | Mentor ↔ student, guardian ↔ ward, leader ↔ follower, employer ↔ employee |

The chosen label describes **your character's role toward their character**. Requesting `mentor` makes your character the mentor and theirs the student after consent. Symmetric relationships such as spouse appear for both characters. These are explicit approved ties, not inferred social assumptions: a friend's friend is not automatically your friend, spouses are not biological relatives, and adoptive ties do not generate blood relationships. Multiple ties can coexist; remove an outdated status with `/relationship remove` when a relationship changes. There is no automatic succession or status-replacement rule yet.

- `/relationship request category my-character user their-character relation` posts a request in the current channel. Choose the required category first, then select a relationship from that category's autocomplete list. No relationships are suggested before a category is selected. Changing the category requires selecting a matching relationship again; submission rejects a relationship from a different category. The receiving owner replies directly to that bot message with `Accept` or `Decline`. Named replies also use the server's scene `reply-name` setting.
- `/relationship requests` privately lists incoming and outgoing requests.
- `/relationship approve request` and `/relationship decline request` are command alternatives to replying.
- `/relationship remove character relationship` removes an approved direct fact involving one of your characters.
- `/relationship view user character [category]` publicly shows direct relationships and background inferences, including the rule that produced each inference.

Inference is recalculated from the complete approved graph rather than permanently stored. Current rules derive siblinghood from a shared parent, grandparent and great-grandparent chains, aunt/uncle and nibling relationships, cousins through sibling parents, and multi-generation ancestors/descendants. Removing a direct fact or deleting a character therefore cascades safely: every unsupported derived relationship disappears, while unrelated direct facts remain. Definitions and path rules are isolated in `Services/RelationshipCatalog.cs`, allowing later political, feudal, or succession rules to use the same storage and graph engine.

The public `relationships.html` atlas supports search, category and inference filters, pan/zoom, draggable nodes, a readable connection ledger, and character-focused links. Its spacious map layout can be switched to a biological family tree: direct parentage establishes generations from older to younger, while siblings and twins stay together and recorded ages sort each generation. Biological lines form a distance heatmap centered on the focused character; enabling inferred ties allows those visible ties to become shorter graph paths. Clicking empty map space opens **Map → Map colors**, where the near and far colors can be customized and saved in that browser. Selecting a node opens its relationship list; double-clicking or using **Center on map** rebuilds the radial layout around that character. Right-clicking a node opens actions for centering it, opening its public record, or copying a focused URL.

Category tabs separate **All ties, Bloodlines, Adoptive family, Social, Romance, and Roles & loyalties**. A relationship-status picker isolates individual types (including their inverse). Social categories open **Social circles**: the focused person is in the center, direct ties form the first ring, and further connections form subsequent rings. Rings measure connection steps, not affection, age, or invented relationships. The ordinary map remains available, and Adoptive family can use the generation-based family tree. Approved social changes publish through the same `/siteadmin` automatic/manual workflow as biological changes.

The local server includes an eight-character **Vale family graph** split between the two requested accounts. Only eight direct facts are seeded, while sibling, grandparent, great-grandparent, pibling/nibling, cousin, ancestor, and descendant results must be inferred. They are ordinary approved characters: they consume sequential OC-role slots and appear in sanitized GitHub Pages exports with the same owner-attribution and ID-privacy rules as other characters. Internal account mappings remain in ignored local JSON.

### Expanded medieval relationships

The website also uses category-first selection: the relationship-status picker stays disabled with **Choose a category first** until a category tab or dropdown is selected. **All ties** still shows the complete graph, while choosing a specific status requires a category and includes that status's inverse.

The catalog now includes the core and A-tier character relationships from the medieval expansion: **132 selectable/inferred perspectives**, including inverses. Existing IDs and approved records remain valid. `societal-student` now displays **Mentee**, and `romantic-engaged` displays **Betrothed**. Search still accepts student, fiancé/fiancée, engaged, husband, and wife. Teacher/pupil and master/apprentice are separate from mentor/mentee; ruler/subject, liege/vassal, and commander/subordinate are separate from the original generic leader/follower. Exact typed terms rank ahead of partial autocomplete matches; accents, spaces, and hyphens do not prevent matching.

| Category | Added relationships |
| --- | --- |
| Social | Childhood friend, close friend, former friend, companion, former enemy, neighbour, travelling companion, lifelong friend, childhood companion, drinking companion, sworn sibling, oath-bound companion, trusted companion, former companion, friendly rival, frenemy, archrival, family rival, chosen/found family, fellow pilgrim; healer ↔ patient, tutor ↔ tutored pupil, host ↔ guest |
| Romantic | Former spouse, lover, former lover, former betrothed, arranged betrothed, political marriage partner, consort, paramour, on-and-off lover, estranged spouse, courting partner, former courting partner |
| Roles & loyalties: family and training | Stepparent ↔ stepchild, stepsibling, in-law, foster parent ↔ foster child; master ↔ apprentice, teacher ↔ pupil, knightly master ↔ squire, patron ↔ protégé |
| Roles & loyalties: political and military | Political ally, political rival, comrade-in-arms, clanmate, diplomatic ally, treaty partner, fellow courtier; liege ↔ vassal, ruler ↔ subject, commander ↔ subordinate, knight's liege ↔ sworn knight, advised monarch ↔ royal advisor, regent ↔ represented monarch, heir's regent ↔ heir under regency, knight's patron ↔ sponsored knight, bodyguard ↔ protected charge, captain ↔ soldier, veteran mentor ↔ recruit, mercenary employer ↔ hired mercenary |
| Roles & loyalties: household and trade | Household employer ↔ servant, noble patron ↔ retainer, household lord ↔ steward, noble contact ↔ commoner contact, merchant buyer ↔ supplier, merchant ↔ customer |

All additions use the existing owner-consent workflow. Each tie represents an established connection; noble/commoner contacts, clanmates, and other shared-status ties are explicitly approved connections between individuals, never automatic matches based on status, location, clan, or house. Consort is the symmetric partnership label. Political marriage and arranged betrothal use Romantic without implying affection. Non-biological stepfamily, in-laws, and foster care use Roles & loyalties so they do not imply adoption or blood relations. They appear in maps, lists, filters, and social circles, but do not establish family-tree generations.

Distinct companionship and romantic-history types remain separate. Artisan/master is an alias for the craft master in master/apprentice; liege/lord is one feudal role. Qualified labels distinguish household employers, mercenary employers, knightly patrons, regents, and other roles that otherwise share a word but have different inverses. Existing employer/employee, guardian/ward, spouse, confidant, rival, and biological half-sibling definitions are reused instead of duplicated.

No new inference or automatic status transition is introduced. A known shared biological parent supports generic siblinghood, never full siblings, half siblings, or twins. Marriage does not infer stepfamily, adoption, or parenthood. A new active or former tie never deletes an existing one; remove outdated direct facts explicitly. Former ties remain visible by default. Inferred ties still recalculate from their supporting facts, while separately approved direct ties persist. One-sided feelings, fantasy ties, the optional specialized list, and house-to-house feuds/rivalries are outside this expansion; houses are not character records.

The atlas reads inverse IDs and perspective wording from the catalog for all directional ties, keeps arrows stable when selecting either character, and shows coexisting labels (with an additional-count indicator when space is limited). The generated `docs/relationship-catalog.json` asset lets the expanded filters and labels work with older live snapshots. It contains only relationship definitions, never characters or owners. See `GITHUB_PAGES_SETUP.md` for regeneration and deployment.

## Overworld and scene tracking

Members with `overworld.worlddate` set the server's current fictional date with `/overworld worlddate date`. Accepted inputs are `dd-mm-yyyy`, `ddmmyyyy`, and `dd/mm/yyyy`; Yoko validates and normalizes them to `dd-mm-yyyy`. Per-server world state is represented by `UniverseData` and stored in `data/universe.json` so more universe properties can be added later.

Members create scenes with `/scenetracker create character day [title]`. The character is autocompleted from the caller's approved characters. The chosen day must exist within the month and year of the current world date. Each scene stores a snapshot of that resulting world date; when no title is supplied, its title is **Scene #N — dd-mm-yyyy**. Numbers increase per server and are not reused after deletion. Custom titles are limited to 100 characters for Discord autocomplete.

### Scene limits and messages

The default is **4 ongoing scenes per character**, not four per account. Creation and accepting an invitation both use a slot; pending invitations and completed scenes do not. Completion, deletion, or removing that character from a scene frees the slot. Capacity is rechecked on acceptance and serialized with creation, so simultaneous requests cannot exceed the limit. If an invited character fills its slots before accepting, the invitation stays pending so they can free a slot and retry.

Admins and the seeded Moderator role can configure these commands:

```text
/scenetracker settings limit:4 reply-name:Helios
/scenetracker slots add user:@Sammi amount:2
/scenetracker slots view user:@Sammi
/scenetracker slots remove user:@Sammi amount:1
/scenetracker slots reset user:@Sammi
```

Bonus slots apply to **each character owned by that user in this server**: a base of 4 plus a bonus of 2 allows 6 ongoing scenes for each of their characters. Bonuses persist across restarts; `reset` removes only the bonus. Base limits and bonuses allow 0–1,000 each. A base of zero closes ordinary creation but still permits users with bonuses. Lowering limits never deletes or completes existing scenes; an over-cap character cannot join/create another until below the new limit. Legacy scene names are migrated to stable character IDs (using aliases where necessary), so renaming a character does not reset their counter. Back up `data/scenes.json` and `data/characters.json` before deploying.

`/scenetracker settings` with no options displays settings. `reply-name:Helios` changes prompts to **Accept, Helios.** and **Decline, Helios.**; `reply-name:auto` uses the bot's current server nickname/display name. Plain `Accept`/`Decline` always works, even for invitations sent before a name change. This also configures named relationship replies, but does not change the separate auto-moderation approval wording.

Customize the public message shown after a scene invite is accepted:

```text
/scenetracker settings acceptance-message:{user}, **{charactername}** has joined **{scene}**. The story continues!
```

Supported placeholders are `{user}` (also `@{user}`), `{charactername}`, `{scene}`, and `{date}`. Markdown is preserved. Templates allow up to 1,500 characters and are checked for placeholder expansion against Discord's 2,000-character message limit. Reply names allow 1–32 characters. The default success message is `{user} accepted. **{charactername}** joined scene **{scene}**.` Settings and bonuses are isolated per server in `data/scenes.json`.

### Scene commands

Active scenes are managed with:

- `/scenetracker invite scene user character` posts a persistent invitation for another member's approved character. Only that invited member can activate it by replying to the exact bot message with `Accept` (or the configured named reply); `Decline` rejects it. Acceptance grants participant access to the scene, subject to that character's ongoing-scene limit.
- `/scenetracker view scene` publicly shows the scene's status, world date, creator, participants, and characters.
- `/scenetracker complete scene` marks the scene completed and removes it from active-scene autocomplete.
- `/scenetracker edit remove-character scene user character` removes one character; a participant with no remaining characters is removed.
- `/scenetracker edit remove-user scene user` removes the member and all their characters.
- `/scenetracker delete scene` permanently deletes an active scene.
- `/scenetracker history` publicly shows every active and completed scene, using public continuation pages when needed.

Members with `scenetracker.manage.own` can manage scenes in which they participate. `scenetracker.manage.any` permits management without participating, and Discord Administrators always bypass both checks. This access remains intentionally trust-based. Completed scenes remain in `data/scenes.json` for history; deleted scenes do not.

## Discord setup

1. In the [Discord Developer Portal](https://discord.com/developers/applications), create an application.
2. Open **Bot**, create the bot, and reset/copy its token. Never commit or share this token.
3. Open **OAuth2 > URL Generator** and select the `bot` and `applications.commands` scopes.
4. Select the **Send Messages** bot permission, then use the generated URL to add it to a test server.
5. Enable Developer Mode under Discord's **User Settings > Advanced**, right-click the test server, and copy its ID.

Under **Bot > Privileged Gateway Intents**, enable both **Server Members Intent** and **Message Content Intent**. Auto moderation needs the member list, and the interactive setup flows need to read the administrator's follow-up messages.

Grant the bot **Manage Messages** in the forms channel so it can remove admin fillout replies. Without that permission, the private prompts still work but replies remain visible.

## Auto moderation

New non-bot members are recorded in `data/users.json` and assigned the server role named **Unverified**. If that role does not exist, the bot attempts to create it. The bot needs **Manage Roles**, and its own role must sit above every role it manages.

At startup and once every 24 hours, Yoko reconciles JSON status against the explicit Discord roles **Verified** and **Unverified**. Members with only Verified are promoted; members with only Unverified are demoted and receive a fresh unverified grace window. Members with both roles are reported as conflicts and left unchanged; members with neither role retain their stored state. Members with `debug.recheck-verified` can run the same reconciliation immediately with `/debug recheck-verified`.

Every two hours the bot evaluates server-specific rules stored in `data/automod-rules.json`. New servers start with editable equivalents of the earlier behavior: `unverified-2day-warn`, `unverified-3day-kick`, and `inactive-30day-warn`.

- `/automod add title` starts a private rule-building wizard.
- `/automod delete title` deletes a rule and its queued approvals.
- `/automod view [title]` lists rules or displays one rule.

The first supported rule type is `time-warn`. Its clock can be `unverified` or `inactive`. Durations may combine weeks, days, hours, minutes, and seconds—for example `30s`, `1h30m`, `2d4s`, or `5d6h`. Rules are evaluated every 30 seconds. A rule can DM the user, announce in a channel, or send no message. Actions are `kick`, `ban`, `mute`, and `none`; mute durations may be from one minute to 28 days.

The bot requires **Kick Members** for kicks, **Ban Members** for bans, and **Moderate Members** for mute/timeouts, in addition to the existing message and role permissions.

Actions can execute immediately or require approval. An approval rule posts its saved template in the chosen channel and persists the pending request. A member with `automod.approve` approves or rejects it by using Discord's **Reply** on that exact bot message and writing `Confirm, Yoko.` or `Cancel, Yoko.`. Available message placeholders are `{user}`, `{automod}`, `{title}`, `{message}`, and `{action}`.

## Member alerts

Member join and leave alerts are server-specific and stored in the ignored local file `data/member-alerts.json`.

- `/alertadmin leave add role:@role` alerts when a member who had that role leaves. Use `edit` to replace its destination or message and `delete` to remove it. Multiple watched roles may be configured; a member matching multiple rules triggers each rule.
- `/alertadmin newaccount set days:7` alerts when a joining member's Discord account is less than seven days old. Running `set` again changes the threshold, destination, or message; `delete` disables it.
- `/alertadmin view` shows every configured alert, destination, threshold, and message.

Add, edit, and set commands use private wizards whose channel replies are deleted. Destinations may be `here`, any mentioned text channel, `dm` for the member who triggered the alert, `dm me`, or `dm @user` for a fixed recipient. A subject DM can fail when that member's Discord privacy settings reject bot messages; channel and fixed-recipient alerts continue independently.

All templates support `{user}`, `{username}`, `{displayname}`, `{userid}`, and `{server}`. Leave alerts additionally support `{role}`, `{rolename}`, and `{joinedat}`. New-account alerts support `{accountage}`, `{accountagedays}`, `{threshold}`, and `{createdat}`. Yoko keeps an in-memory role snapshot for each member so leave alerts remain accurate after Discord removes the departed member from the server cache.

## Recurring messages and reusable reminders

Both features use slash commands and keep server-specific configuration, message selection, and pending countdowns in the ignored local file `data/scheduled-messages.json`. Include this file when moving or backing up the bot. Destinations may be `here`, a channel mention, a user mention for a fixed DM recipient, or `dm me`. The destination must belong to the current server; channel delivery requires Yoko to be able to view and send there. A user destination receives a DM, rather than a ping in the command channel. Names are case-insensitive and can contain up to 100 letters, digits, hyphens, or underscores. Timespans accept combined units such as `30m`, `2h`, `1h30m`, or `1d`, from one minute to 365 days. Messages may contain up to 2,000 characters.

### Automessages

```text
/automessage create name:prompts destination:#general timespan:2h mode:pseudorandom
/automessage edit name:prompts
/automessage mode name:prompts mode:incremental
/automessage pause name:prompts
/automessage resume name:prompts
/automessage list
/automessage delete name:prompts
```

Creation opens the same private editor as `edit`. It displays a numbered list and accepts channel replies: `add Hello there, everyone!`, `edit 1` followed by the replacement message, `delete 3`, and `done` to close. Changes save immediately; the bot deletes replies when permitted. Editing sessions expire after ten minutes and recheck the editor's permission before changing anything. The first message starts the countdown; an empty list sends nothing. Paused schedules retain their messages.

Modes are `pseudorandom` (a shuffled cycle through every message, avoiding consecutive repeats between cycles when multiple messages exist), `incremental` (list order, looping back to the start), and `random` (independent choices that can repeat). Mode and message-list changes reset selection progress.

### Bump reminders

Step 1 — a manager saves the reminder's settings:

```text
/reminder name:bump timespan:2h destination:#bump message:Time to bump the server!
```

Setup selects this reminder for the server's `/remind` command and leaves it idle. It does not start a countdown.

Step 2 — whenever someone bumps the server, they start the saved countdown with no fields at all:

```text
/remind
```

The reminder sends the saved message to the saved destination once, then becomes idle until `/remind` is used again. Activating a pending reminder reports its existing due time without resetting or stacking countdowns, including simultaneous uses by different members. The command does not detect bumps automatically.

`/reminder` is for management only. Its `name` is a manager-facing template name; users never need it when running `/remind`. Supplying settings saves them (equivalent to `action:setup`). A new reminder requires `timespan`, `destination`, and `message`; an existing reminder accepts partial edits, such as `/reminder name:bump timespan:3h`. Saving settings selects that reminder and cancels its old pending countdown so the next `/remind` starts from the updated settings. Omitted settings are preserved.

Use `/reminder name:bump` or `action:view` to inspect settings and delivery errors, `action:cancel` to stop a pending countdown while keeping its settings, or `action:delete` to remove it. If several templates are saved, `/reminder name:bump action:use` chooses which one `/remind` runs without starting it. Choosing another template does not cancel previously scheduled sends; cancel those explicitly if needed. Deleting the selected template disables `/remind` until a manager saves or selects another one.

Existing installations with exactly one saved reminder automatically use it for `/remind`, preserving any pending countdown. Servers with several legacy reminders require a manager to select one with `action:use`. Selection is per server and persists across restarts. With nothing configured, `/remind` explains that a manager must finish setup; it never creates a blank reminder.

Every `/reminder` and `/remind` response is public in the invoking channel, including setup, view, validation, permission, and countdown responses. None is ephemeral. Scheduled delivery still uses the configured channel or member DM destination.

Admins can configure both features through their normal wildcard grant. Verified members and moderators receive `reminder.activate`, including a one-time migration for existing servers. `automessage.manage` and `reminder.manage` can be granted separately through `/permissions`; for example, `/permissions grant role:@Moderator permission:reminder.manage`.

The scheduler resumes persisted countdowns at startup and delivers at most one overdue automessage per schedule, rather than replaying every missed interval. Due state is saved before delivery to avoid duplicate reminders after an uncertain send. If Discord rejects a delivery, Yoko records the error; reminders remain idle until someone activates them again. There is no automatic retry, and a process interruption between saving the due state and sending can lose that delivery. View the saved state to investigate missing permissions or closed DMs.

## Verification profiles

`/verify user type` marks a member verified and applies exactly the added/removed role lists stored in that profile. No roles are implicit: include **Verified** in the added list and **Unverified** in the removed list when that is the desired workflow. The built-in profile names are `writer` and `spectator`, ready to be configured. Character approval does not verify a member implicitly because staff must choose the verification type.

Verification configuration is server-specific and stored in `data/verification-settings.json`:

- `/verifyadmin role add type`
- `/verifyadmin role edit type`
- `/verifyadmin role delete type`

Add and edit open a private two-step wizard. Mention every role users should receive in one channel reply, then mention every role that should be removed in the next. The bot deletes both replies immediately and updates the ephemeral prompt. `none` clears a list, `keep` preserves the current list during editing, and `cancel` exits. Deleting a profile never deletes Discord roles.

Use `/verifyadmin successmessage channel:#general` to start a private setup prompt, then reply in the current channel with the complete message. The bot stores Markdown, emoji, mentions, and formatting verbatim, deletes the setup reply, and updates the ephemeral prompt. Both `{user}` and `@{user}` are replaced with the verified member's mention when the saved message is posted.

## Development checks

Run `dotnet run --project tests/Yoko.Checks/Yoko.Checks.csproj` for isolated scene-cap, migration, relationship-consent, owner-privacy/export, bulk-deletion safety, permission, command-registration, automessage-selection, restart-recovery, and one-shot-reminder checks. The checks write only to a fresh system temporary directory; they do not connect to Discord or publish to GitHub. Simulated publishing uses an in-memory HTTP handler, and scheduled message checks use an injected sender and clock. Run `node tests/owner-ui.test.mjs` for attribution rendering and clipboard checks. Run `node tests/preview.mjs` to preview the site with fabricated social/family ties, public/hidden example owners, and Helios branding at `http://127.0.0.1:4188/`; this does not modify stored or published characters.

## Run in VS Code

1. Install the recommended **C# Dev Kit** extension when prompted.
2. Open the ignored `local.settings.json` file in the repository root.
3. Paste the Discord bot token and GitHub token. The current test server ID is already filled in; add a default channel ID when one is needed.
4. Press `F5` and select **Run Yoko Bot**. This launch option reads the file without prompting.
5. Once the console says the command was registered, use `/ping` in the test server.

The file has this shape:

```json
{
  "discordBotToken": "paste-discord-bot-token-here",
  "discordTestGuildId": "paste-server-id-here",
  "discordDefaultChannelId": "paste-channel-id-here",
  "githubPagesToken": "paste-fine-grained-github-token-here"
}
```

`discordTestGuildId` must be the **server ID**, not a channel ID; it controls instant slash-command registration. `discordDefaultChannelId` is stored separately for upcoming channel-default behavior and is not used by a command yet. `local.settings.json` is excluded by `.gitignore`. A safe tracked template is available in `local.settings.example.json`.

Environment variables override local-file values. Select **Run Yoko Bot (prompt for secrets)** if you prefer the original password prompts.

## Host on another Windows PC

After cloning or pulling the repository, double-click **`Start-Yoko.cmd`**. The launcher always runs Yoko from the correct repository folder, checks for a compatible .NET 8 SDK, restores dependencies, and uses the ignored `local.settings.json`. If that file is missing, the launcher creates it from the safe template and opens it in Notepad. It never prints the saved tokens. After an unexpected crash it waits five seconds and restarts the bot; a clean `/shutdown` stops it without restarting.

For a first-time handoff:

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and clone or pull Yoko.
2. Securely copy `local.settings.json` from the current host into the new repository root, or let the launcher create it and paste the tokens locally. Never commit or send this file through GitHub.
3. To preserve live characters and configuration, securely copy the current host's `data/*.json` files into the new clone's `data` folder. These files are also ignored by Git and do not arrive in a pull.
4. Stop Yoko on the old computer, then double-click `Start-Yoko.cmd` on the new computer. Do not run the same Discord bot token on both computers simultaneously.

The GitHub Pages token is optional when this PC will not publish site updates; set `githubPagesToken` to an empty string in that case. The Discord bot token is required. For terminal use, `./Start-Yoko.ps1 -ValidateOnly` performs setup checks without starting the bot, while `-NoRestart` disables crash restarts.

## Run from PowerShell

```powershell
$env:DISCORD_BOT_TOKEN = "paste-token-here"
$env:DISCORD_TEST_GUILD_ID = "paste-server-id-here"
$env:GITHUB_PAGES_TOKEN = "paste-fine-grained-token-here"
dotnet run
```

Remove the environment variables afterward if desired:

```powershell
Remove-Item Env:DISCORD_BOT_TOKEN
Remove-Item Env:DISCORD_TEST_GUILD_ID
Remove-Item Env:GITHUB_PAGES_TOKEN
```

If `DISCORD_TEST_GUILD_ID` is omitted, `/ping` is registered globally and may take Discord up to an hour to appear.
