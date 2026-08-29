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

After selecting a user, Discord autocompletes that user's available character names. Edit/remove commands also autocomplete the baseline fields and any custom fields already stored on the selected character.

Every new character reference defaults to `link/sheet`; set its URL with the `reference` field. Region currently accepts text, with a dedicated validation point ready for the future region catalog.

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
