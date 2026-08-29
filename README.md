# Yoko Discord Bot

A minimal .NET 8 Discord bot using Discord.Net. It provides `/ping` and an administrator-only `/shutdown` Ouija-board sequence.

## Character workflow

Character data is stored locally in `data/characters.json`, keyed first by Discord server ID and then by the owner's Discord user ID. Characters with the same owner are therefore isolated between servers. The file is excluded from Git because it contains live server data. Back it up separately before deploying or moving the bot.

Administrator commands:

- `/character approve user character-name [age] [gender] [region]` creates the character. Age, gender, and region can be supplied immediately; a private prompt asks only for remaining baseline fields. Reply `skip` to leave a value empty, or `stop`/`end` to save and exit early. The bot immediately deletes each channel reply and updates the ephemeral prompt. Discord does not permit truly empty messages.
- `/character edit user character-name field value` changes a field. Baseline names are `name`, `age`, `gender`, `region`, `occupation`, `reference`, `reference-kind`, and `reference-format`. Any other name creates a flexible custom property.
- `/character remove-field user character-name field` clears a baseline value or deletes a custom property.
- `/character view user character-name` previews the current record privately.
- `/character delete user character-name` starts permanent deletion. The admin must then type `confirm CharacterName`; the bot deletes that reply and removes the entire character record. `cancel` stops deletion.

The server-wide default character structure is managed with `/charadmin properties view`, `/charadmin properties add`, and `/charadmin properties remove`. Adding a property makes it appear in approval fillouts and character views for everyone. Removing a default does not destroy character-specific values already stored under that property.

Suggested approval values are managed with `/charadmin autofill add field value` and `/charadmin autofill remove field value`. Adding a suggestion for an unknown field also adds that field to the default structure. Suggestions appear in the approval command for its built-in quick fields and inside the private fillout prompt for dynamic fields. Settings are isolated by server and stored in `data/character-settings.json`.

Character approval roles are configured with `/charadmin roles add` and `/charadmin roles remove`. Each command opens a private prompt and deletes the administrator's reply. The **add** reply replaces the complete add configuration: prefix always-added roles with `*`, then mention the numbered OC roles left-to-right in their exact intended order—for example, `* @Member @1st-OC @2nd-OC @3rd-OC`. The unstarred role count becomes the per-user character cap. Approving character N ensures every starred default is present, grants the first N sequential roles, and stores `ocRoleIndex: N` on that character.

The **remove** reply replaces the list of fixed roles removed whenever a character is approved, such as `@No OC`; it does not edit or delete roles from the numbered ladder. Reply `none` in either setup to clear that configuration. Deleting a character removes the now-unused numbered role and compacts every later character index and role. Starred defaults remain untouched. The configuration is reconciled again when Yoko starts. Yoko needs **Manage Roles**, and its highest role must be above every configured role.

Use `/charadmin approvemessage add` to configure messages sent when a character is approved. The private wizard first asks for a destination: reply `dm`, `here` to dynamically use whichever channel `/character approve` is later invoked in, or mention a fixed text channel such as `#general`. Its next reply is stored verbatim as the message template, including Markdown and emoji. `{user}` becomes the character owner's mention and `{charactername}` becomes the approved character's name. After saving, the wizard asks whether to add another message and repeats the destination/template flow when the answer is `yes`.

Each add-wizard run appends messages to the existing list. `/charadmin approvemessage delete` displays a numbered destination/template summary and accepts comma-separated selections such as `1`, `1,3`, or `1,4,5`. Reply `clear` or `none` at an add-wizard destination prompt to remove every configured approval message. A delivery failure does not undo character approval; the private approval response reports how many configured messages succeeded or failed.

After selecting a user, Discord autocompletes that user's available character names. Edit/remove commands also autocomplete the baseline fields and any custom fields already stored on the selected character.

Every new character reference defaults to `link/sheet`; set its URL with the `reference` field. Region currently accepts text, with a dedicated validation point ready for the future region catalog.

## Overworld and scene tracking

Administrators set the server's current fictional date with `/overworld worlddate date`. Accepted inputs are `dd-mm-yyyy`, `ddmmyyyy`, and `dd/mm/yyyy`; Yoko validates and normalizes them to `dd-mm-yyyy`. Per-server world state is represented by `UniverseData` and stored in `data/universe.json` so more universe properties can be added later.

Members create scenes with `/scenetracker create character day [title]`. The character is autocompleted from the caller's approved characters. The chosen day must exist within the month and year of the current world date. Each scene stores a snapshot of that resulting world date; when no title is supplied, the formatted scene date becomes its title.

Active scenes are managed with:

- `/scenetracker invite scene user character` posts a persistent invitation for another member's approved character. Only that invited member can activate it by replying to the exact bot message with `Accept, Yoko.`; `Decline, Yoko.` rejects it. Acceptance grants participant access to the scene.
- `/scenetracker view scene` publicly shows the scene's status, world date, creator, participants, and characters.
- `/scenetracker complete scene` marks the scene completed and removes it from active-scene autocomplete.
- `/scenetracker edit remove-character scene user character` removes one character; a participant with no remaining characters is removed.
- `/scenetracker edit remove-user scene user` removes the member and all their characters.
- `/scenetracker delete scene` permanently deletes an active scene.
- `/scenetracker history` publicly shows every active and completed scene, using public continuation pages when needed.

Scene participants and server administrators can manage a scene. This access is intentionally trust-based. Completed scenes remain in `data/scenes.json` for history; deleted scenes do not.

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

At startup and once every 24 hours, Yoko reconciles JSON status against the explicit Discord roles **Verified** and **Unverified**. Members with only Verified are promoted; members with only Unverified are demoted and receive a fresh unverified grace window. Members with both roles are reported as conflicts and left unchanged; members with neither role retain their stored state. Administrators can run the same reconciliation immediately with `/debug recheck-verified`.

Every two hours the bot evaluates server-specific rules stored in `data/automod-rules.json`. New servers start with editable equivalents of the earlier behavior: `unverified-2day-warn`, `unverified-3day-kick`, and `inactive-30day-warn`.

- `/automod add title` starts a private rule-building wizard.
- `/automod delete title` deletes a rule and its queued approvals.
- `/automod view [title]` lists rules or displays one rule.

The first supported rule type is `time-warn`. Its clock can be `unverified` or `inactive`. Durations may combine weeks, days, hours, minutes, and seconds—for example `30s`, `1h30m`, `2d4s`, or `5d6h`. Rules are evaluated every 30 seconds. A rule can DM the user, announce in a channel, or send no message. Actions are `kick`, `ban`, `mute`, and `none`; mute durations may be from one minute to 28 days.

The bot requires **Kick Members** for kicks, **Ban Members** for bans, and **Moderate Members** for mute/timeouts, in addition to the existing message and role permissions.

Actions can execute immediately or require approval. An approval rule posts its saved template in the chosen channel and persists the pending request. An administrator approves or rejects it by using Discord's **Reply** on that exact bot message and writing `Confirm, Yoko.` or `Cancel, Yoko.`. Available message placeholders are `{user}`, `{automod}`, `{title}`, `{message}`, and `{action}`.

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
2. Press `F5` and select **Run Yoko Bot**.
3. Paste the bot token into the password prompt and the test server ID into the next prompt.
4. Once the console says the command was registered, use `/ping` in the test server.

The token is passed only to the launched process; it is not written into the repository.

## Run from PowerShell

```powershell
$env:DISCORD_BOT_TOKEN = "paste-token-here"
$env:DISCORD_TEST_GUILD_ID = "paste-server-id-here"
dotnet run
```

Remove the environment variables afterward if desired:

```powershell
Remove-Item Env:DISCORD_BOT_TOKEN
Remove-Item Env:DISCORD_TEST_GUILD_ID
```

If `DISCORD_TEST_GUILD_ID` is omitted, `/ping` is registered globally and may take Discord up to an hour to appear.
