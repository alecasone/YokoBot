# Yoko Character Archive: GitHub Pages setup

The initial scaffold lives in `docs/` so the existing public `YokoBot` repository can publish it directly. It contains only fabricated sample characters. Yoko's ignored `data/*.json` files are not copied or exposed. Automated data commits use a dedicated `pages` branch so they never make the normal `main` source branch fall behind.

## Turn on GitHub Pages

1. Commit and push the new `docs/` scaffold to the repository's `main` branch.
2. On GitHub, open the repository's branch selector and create a branch named `pages` from the newly updated `main` branch.
3. Open `https://github.com/alecasone/YokoBot`.
4. Select **Settings**.
5. In the left sidebar, select **Pages**.
6. Under **Build and deployment**, set **Source** to **Deploy from a branch**.
7. Select branch **pages** and folder **/docs**, then save.
8. After the first deployment finishes, open `https://alecasone.github.io/YokoBot/`.

GitHub may take several minutes to publish a new commit. Check the repository's **Actions** tab if the Pages deployment fails.

## Preview locally

From the `YokoBot` repository:

```powershell
python -m http.server 8080 --directory docs
```

Then open `http://localhost:8080/` for the character directory or `http://localhost:8080/relationships.html` for the interactive relationship atlas. Do not open the HTML files directly because browsers normally block their JSON request when loaded as local files.

## Public data boundary

The Pages site reads only `docs/data/characters.json`. Its public contract contains:

- a random character `publicId` used for stable links;
- character name and optional aliases;
- public owner attribution: `owner.displayName` and, unless the owner opts out, a string `owner.discordId`;
- approved character fields and custom properties;
- an optional public character-reference URL; and
- approval time for sorting;
- the complete relationship catalog, including display names, categories, and inverse type IDs;
- optional public site branding; and
- approved and inferred connections between character `publicId` values, including a human-readable inference explanation.

Schema version **4** adds owner attribution to each character. For example, a fabricated public owner is `"owner": { "displayName": "Example writer", "discordId": "111111111111111111" }`; an opted-out owner is `"owner": { "displayName": "Example writer" }`. Discord IDs must stay strings so JavaScript does not round them. Older snapshots without owner information still load, with an `Owner not published` fallback.

Role IDs, verification state, moderation history, private approval metadata, pending relationship requests, internal relationship IDs, and the full internal mapping in `data/public-identities.json` remain excluded. Public owner display names and optional Discord IDs are the intentional exception to the earlier character-only policy. Both pages explain this in their footer.

`data/public-identities.json` retains existing random account IDs and stores display names and per-server ID-privacy preferences. It remains local and ignored by Git. Character `publicId` values are still stable random IDs used by links and relationship edges; exposing the owner's Discord ID does not replace those keys. Include this local file in host backups/transfers so opt-outs survive a move.

### Owner names and ID privacy

Character cards, character details, and the relationship inspector show **By {display name}**. Public owners also have a **Copy Discord ID** button. `/character anonymize-website-discord-id` is available to everyone for their own account, with no role grant required. It hides the ID for all of that member's current and future characters in the current server. `enabled:false` makes the ID public again. Display names remain visible in either case.

The preference persists across restarts and character purges. Opted-out IDs are omitted from exported JSON, not hidden with CSS. Changing the preference attempts to publish a fresh complete snapshot immediately, even when routine auto-publishing is off. On a publishing failure, the command clearly reports that the choice was saved but the live site did not update; fix the configuration/credential/network issue and use `/siteadmin publish`. After a successful commit, wait for the Pages deployment. The automatic-publishing setting is not changed.

**This is not full anonymity or historical erasure.** Old Git commits, browser caches, and third-party copies can retain previously published IDs. IDs manually entered in character fields or links are not automatically scrubbed. Cached names are refreshed when available; an owner whose name has never been available shows `Unknown member`.

## Create Yoko's GitHub credential

Create a fine-grained personal access token in GitHub:

1. Open GitHub **Settings**.
2. Open **Developer settings → Personal access tokens → Fine-grained tokens**.
3. Create a token owned by the account that owns `YokoBot`.
4. Under **Repository access**, choose **Only select repositories** and select `YokoBot`.
5. Under **Repository permissions**, grant **Contents: Read and write**. No other repository permission is required.
6. Choose an expiration, generate the token, and copy it immediately.

Paste this value into `githubPagesToken` in the ignored `local.settings.json` file. Yoko loads it into the process without committing it to the repository. The optional **Run Yoko Bot (prompt for secrets)** VS Code configuration can still pass it as `GITHUB_PAGES_TOKEN` instead.

## Connect Yoko

After restarting the bot so `/siteadmin` is registered:

1. Run `/siteadmin setup repository:alecasone/YokoBot`. The defaults are branch `pages`, data path `docs/data/characters.json`, and site URL `https://alecasone.github.io/YokoBot/`.
2. Run `/siteadmin publish`. This replaces the fabricated sample records with the current sanitized character directory.
3. Run `/siteadmin status` and confirm the token is loaded, no local changes are pending, and a short commit hash is shown.
4. Run `/siteadmin autopublish enabled:true`.

Character creation, supplied approval fields, fillout replies, edits, removed fields, aliases, renames, confirmed deletion (including bulk purges), relationship approval, relationship removal, observed owner display-name changes, and site-branding changes now mark the directory pending. Yoko waits 20 seconds after the latest change and then publishes one complete snapshot. Rapid changes are combined, and GitHub writes are serialized to prevent update conflicts. Privacy changes use the immediate-publish exception described above.

Yoko updates only `docs/data/characters.json` through GitHub's Contents API. It never runs `git add .` or commits the local working tree, because unrelated source changes may be present. If GitHub is unavailable, the local character operation still succeeds and `/siteadmin status` reports a pending snapshot and the last error. `/siteadmin publish` retries immediately.

Deleting a character will remove it from the current public snapshot. Previous versions can remain in Git history, so only deliberately public roleplay data belongs in the export.

## Updating the site design later

Keep editing `docs/index.html`, `docs/relationships.html`, `docs/styles.css`, `docs/app.js`, `docs/relationships.js`, `docs/branding.js`, `docs/owner.js`, `docs/site-config.json`, and other site assets on `main` with the bot source. Copy or merge those asset changes into `pages`, then run `/siteadmin publish` again so the live character and relationship JSON is refreshed. Do not treat the sample `docs/data/characters.json` on `main` as live server data.

The publisher only updates JSON: it cannot ship new tabs, scripts, styles, or HTML. For the social/branding/owner-attribution update, deploy **all eight listed asset files together**, restart the host bot with the updated source, and publish a new snapshot. This also replaces the old privacy disclaimers and supplies the social catalog, configured branding, and owner information to the new UI. Do not replace the live Pages data file with the sample file from `main`.

## Website name and transparent logo letter

The default public name remains Yoko until configured. To use Helios:

```text
/siteadmin branding name:Helios logo-letter:H
/siteadmin branding archive-title:Character Archive atlas-title:Relationship Atlas tagline:The lives and stories of our world.
```

Run `/siteadmin branding` without options to view the bot's saved settings. It requires `site.configure`. Changes are per server, saved in ignored `data/site-settings.json`, and included in the sanitized snapshot. They publish automatically when enabled, or with `/siteadmin publish`.

For static-only customization, edit `docs/site-config.json` instead. It has `name`, `logoLetter`, `archiveTitle`, `atlasTitle`, and `tagline`. A bot-published `branding` object takes precedence over that file; otherwise the file overrides the built-in defaults. Change both name and logoLetter when editing the static file. The logo is an SVG text letter with a transparent background over the existing circular ornament, not a baked-in bitmap. Branding is inserted as text, never HTML.

Name allows 60 characters, each title 80, tagline 240, and the logo one Unicode letter. Runtime branding updates headings, browser titles, descriptive metadata, and footer references on both pages. Link-preview crawlers may not execute JavaScript: static metadata and `docs/og.png` are separate assets and should also be customized when changing the sharing card. The GitHub repository URL is not changed by branding. Invitation wording is a separate setting: `/scenetracker settings reply-name:Helios`.
