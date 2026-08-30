# Yoko Discord Bot

A .NET 8 Discord bot using Discord.Net for character management, verification, roleplay scenes, auto moderation, and an ominous `/shutdown` Ouija-board sequence.

## Command permissions

Yoko uses an internal, per-server PEX-style permission system stored in `data/permissions.json`. Discord server Administrators always bypass the file. Other members receive permissions through stable Discord role IDs or optional direct user grants. Exact names such as `character.approve`, section wildcards such as `character.*`, and the global `*` wildcard are supported.

The first time a server checks permissions, Yoko seeds these role assignments:

- **Admin** (`1541979162242195466`) receives `*`.
- **Moderator** (`1541979611754135643`) receives character approval/edit/view/delete access, verification, auto-moderation viewing and approvals, the verification recheck, every scene-tracker permission, biological relationship access, and permission viewing.
- **Verified** (`1542018931894521887`) receives `/ping`, self-scoped character edit/view/delete, scene creation/view/history, management of scenes in which they participate, and biological relationship access.

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
| `debug.recheck-verified` | `/debug recheck-verified` |
| `overworld.worlddate` | `/overworld worlddate` |
| `scenetracker.create` | `/scenetracker create` |
| `scenetracker.view` | `/scenetracker view` |
| `scenetracker.history` | `/scenetracker history` |
| `scenetracker.manage.own` | `/scenetracker invite`, `/scenetracker complete`, `/scenetracker delete`, and both `/scenetracker edit` actions, but only for scenes in which the command user participates |
| `scenetracker.manage.any` | The same scene-management commands for any scene, without needing to participate |
| `permissions.view` | `/permissions list`, `/permissions view`, and `/permissions role` |
| `permissions.manage` | `/permissions grant`, `/permissions revoke`, `/permissions grant-user`, and `/permissions revoke-user`; also permits all permission-viewing commands |
| `site.view` | `/siteadmin status` |
| `site.publish` | `/siteadmin publish`; also permits `/siteadmin status` |
| `site.configure` | `/siteadmin setup` and `/siteadmin autopublish`; also permits publishing and status |
| `relationship.request` | `/relationship request` for one of your characters |
| `relationship.respond` | `/relationship requests`, `/relationship approve`, `/relationship decline`, and replying `Accept` or `Decline` to your incoming request |
| `relationship.remove` | `/relationship remove` for a direct relationship involving one of your characters |
| `relationship.view` | `/relationship view` for direct and inferred biological relationships |

Wildcard grants affect every matching permission in this table. For example, `character.*` grants every character and charadmin permission, while `character.configure.*` grants only the four charadmin configuration sections. The global `*` grants every current and future permission.

## Public character archive

The GitHub Pages scaffold in `docs/` is a searchable master directory built from sanitized character-only JSON. It intentionally excludes Discord usernames and IDs, verification state, roles, moderation data, and private administrative metadata. Follow `GITHUB_PAGES_SETUP.md` to preview it locally and publish it from the dedicated `pages` branch and `/docs` folder. Keeping bot-generated data commits off `main` prevents them from interfering with normal source-code work.

Characters have stable random `publicId` values, so renames do not break public links. Renaming a character automatically preserves the previous name as an alias; `/character edit` can also set the `aliases` field from a comma-separated list, and `/character remove-field` can clear it. Discord users receive separate stable random IDs in the ignored local file `data/public-identities.json`; the private Discord-ID mapping is not exported to Pages.

`/siteadmin setup` configures the repository, branch, JSON path, and public URL. `/siteadmin publish` publishes one complete sanitized snapshot immediately, `/siteadmin autopublish` controls automatic publication, and `/siteadmin status` reports pending changes, token availability, the last attempt, the last successful commit, and any error. Character approval, approval-fillout values, edits, removed fields, aliases, renames, and confirmed deletion all mark the snapshot dirty. Automatic publishing waits 20 seconds after the most recent change so a burst of edits becomes one commit.

## Character workflow

Character data is stored locally in `data/characters.json`, keyed first by Discord server ID and then by the owner's Discord user ID. Characters with the same owner are therefore isolated between servers. The file is excluded from Git because it contains live server data. Back it up separately before deploying or moving the bot.

Character commands (subject to the permission nodes above):

- `/character approve user character-name [age] [gender] [region]` creates the character. Age, gender, and region can be supplied immediately; a private prompt asks only for remaining baseline fields. Reply `skip` to leave a value empty, or `stop`/`end` to save and exit early. The bot immediately deletes each channel reply and updates the ephemeral prompt. Discord does not permit truly empty messages.
- `/character edit user character-name field value` changes a field. Baseline names are `name`, `age`, `gender`, `region`, `occupation`, `reference`, `reference-kind`, and `reference-format`. Any other name creates a flexible custom property.
- `/character remove-field user character-name field` clears a baseline value or deletes a custom property.
- `/character view user character-name` previews the current record privately.
- `/character delete user character-name` starts permanent deletion. The command user must then type `confirm CharacterName`; the bot deletes that reply and removes the entire character record. `cancel` stops deletion.

The server-wide default character structure is managed with `/charadmin properties view`, `/charadmin properties add`, and `/charadmin properties remove`. Adding a property makes it appear in approval fillouts and character views for everyone. Removing a default does not destroy character-specific values already stored under that property.

Suggested approval values are managed with `/charadmin autofill add field value` and `/charadmin autofill remove field value`. Adding a suggestion for an unknown field also adds that field to the default structure. Suggestions appear in the approval command for its built-in quick fields and inside the private fillout prompt for dynamic fields. Settings are isolated by server and stored in `data/character-settings.json`.

Character approval roles are configured with `/charadmin roles add` and `/charadmin roles remove`. Each command opens a private prompt and deletes the administrator's reply. The **add** reply replaces the complete add configuration: prefix always-added roles with `*`, then mention the numbered OC roles left-to-right in their exact intended order—for example, `* @Member @1st-OC @2nd-OC @3rd-OC`. The unstarred role count becomes the per-user character cap. Approving character N ensures every starred default is present, grants the first N sequential roles, and stores `ocRoleIndex: N` on that character.

The **remove** reply replaces the list of fixed roles removed whenever a character is approved, such as `@No OC`; it does not edit or delete roles from the numbered ladder. Reply `none` in either setup to clear that configuration. Deleting a character removes the now-unused numbered role and compacts every later character index and role. Starred defaults remain untouched. The configuration is reconciled again when Yoko starts. Yoko needs **Manage Roles**, and its highest role must be above every configured role.

Use `/charadmin approvemessage add` to configure messages sent when a character is approved. The private wizard first asks for a destination: reply `dm`, `here` to dynamically use whichever channel `/character approve` is later invoked in, or mention a fixed text channel such as `#general`. Its next reply is stored verbatim as the message template, including Markdown and emoji. `{user}` becomes the character owner's mention and `{charactername}` becomes the approved character's name. After saving, the wizard asks whether to add another message and repeats the destination/template flow when the answer is `yes`.

Each add-wizard run appends messages to the existing list. `/charadmin approvemessage delete` displays a numbered destination/template summary and accepts comma-separated selections such as `1`, `1,3`, or `1,4,5`. Reply `clear` or `none` at an add-wizard destination prompt to remove every configured approval message. A delivery failure does not undo character approval; the private approval response reports how many configured messages succeeded or failed.

After selecting a user, Discord autocompletes that user's available character names. Edit/remove commands also autocomplete the baseline fields and any custom fields already stored on the selected character.

Every new character reference defaults to `link/sheet`; set its URL with the `reference` field. Region currently accepts text, with a dedicated validation point ready for the future region catalog.

## Biological relationships

Approved relationships are stored per server in the ignored local file `data/relationships.json`. Records use stable character `publicId` values, so character renames do not break them. A direct record stores one perspective and Yoko automatically supplies the inverse perspective—for example, biological parent ↔ biological child. Pending requests and Discord owner IDs remain local and are not exported to the public site.

The initial biological catalog contains:

- biological parent ↔ child;
- biological sibling, full sibling, half sibling, and twin;
- biological grandparent ↔ grandchild and great-grandparent ↔ great-grandchild;
- biological aunt/uncle/pibling ↔ niece/nephew/nibling;
- biological cousin; and
- inferred biological ancestor ↔ descendant.

Autocomplete recognizes neutral labels and aliases such as mother, father, son, daughter, brother, sister, aunt, uncle, niece, and nephew. When the selected pair already has a relationship implied by the family graph, that relationship is ranked first and marked **inferred from family graph**.

- `/relationship request my-character user their-character relation` posts a request in the current channel. The receiving owner replies directly to that bot message with `Accept` or `Decline`.
- `/relationship requests` privately lists incoming and outgoing requests.
- `/relationship approve request` and `/relationship decline request` are command alternatives to replying.
- `/relationship remove character relationship` removes an approved direct fact involving one of your characters.
- `/relationship view user character` publicly shows direct relationships and background inferences, including the rule that produced each inference.

Inference is recalculated from the complete approved graph rather than permanently stored. Current rules derive siblinghood from a shared parent, grandparent and great-grandparent chains, aunt/uncle and nibling relationships, cousins through sibling parents, and multi-generation ancestors/descendants. Removing a direct fact or deleting a character therefore cascades safely: every unsupported derived relationship disappears, while unrelated direct facts remain. Definitions and path rules are isolated in `Services/RelationshipCatalog.cs`, allowing later categories such as adopted, political, feudal, or succession relationships to use the same storage and graph engine.

The local server includes an eight-character **Vale family graph** split between the two requested accounts. Only eight direct facts are seeded, while sibling, grandparent, great-grandparent, pibling/nibling, cousin, ancestor, and descendant results must be inferred. They are ordinary approved characters: they consume sequential OC-role slots and appear in sanitized GitHub Pages exports. Account mappings remain only in ignored local JSON and are not documented in the public repository.

## Overworld and scene tracking

Members with `overworld.worlddate` set the server's current fictional date with `/overworld worlddate date`. Accepted inputs are `dd-mm-yyyy`, `ddmmyyyy`, and `dd/mm/yyyy`; Yoko validates and normalizes them to `dd-mm-yyyy`. Per-server world state is represented by `UniverseData` and stored in `data/universe.json` so more universe properties can be added later.

Members create scenes with `/scenetracker create character day [title]`. The character is autocompleted from the caller's approved characters. The chosen day must exist within the month and year of the current world date. Each scene stores a snapshot of that resulting world date; when no title is supplied, the formatted scene date becomes its title.

Active scenes are managed with:

- `/scenetracker invite scene user character` posts a persistent invitation for another member's approved character. Only that invited member can activate it by replying to the exact bot message with `Accept, Yoko.`; `Decline, Yoko.` rejects it. Acceptance grants participant access to the scene.
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

## Verification profiles

`/verify user type` marks a member verified and applies exactly the added/removed role lists stored in that profile. No roles are implicit: include **Verified** in the added list and **Unverified** in the removed list when that is the desired workflow. The built-in profile names are `writer` and `spectator`, ready to be configured. Character approval does not verify a member implicitly because staff must choose the verification type.

Verification configuration is server-specific and stored in `data/verification-settings.json`:

- `/verifyadmin role add type`
- `/verifyadmin role edit type`
- `/verifyadmin role delete type`

Add and edit open a private two-step wizard. Mention every role users should receive in one channel reply, then mention every role that should be removed in the next. The bot deletes both replies immediately and updates the ephemeral prompt. `none` clears a list, `keep` preserves the current list during editing, and `cancel` exits. Deleting a profile never deletes Discord roles.

Use `/verifyadmin successmessage channel:#general` to start a private setup prompt, then reply in the current channel with the complete message. The bot stores Markdown, emoji, mentions, and formatting verbatim, deletes the setup reply, and updates the ephemeral prompt. Both `{user}` and `@{user}` are replaced with the verified member's mention when the saved message is posted.

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
