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

Then open `http://localhost:8080/`. Do not open `docs/index.html` directly because browsers normally block its JSON request when loaded as a local file.

## Public data boundary

The Pages site reads only `docs/data/characters.json`. Its public contract contains:

- a random character `publicId` used for stable links;
- character name and optional aliases;
- approved character fields and custom properties;
- an optional public character-reference URL; and
- approval time for sorting.

It must never contain Discord user IDs, usernames, role IDs, verification state, moderation history, private approval metadata, or the mapping in `data/public-identities.json`.

Each Discord user receives a stable random ID in the local-only `data/public-identities.json`. That mapping survives moderation-record removal and can support public ownership aliases later without revealing the Discord account. It is intentionally absent from the first directory export.

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

Character creation, supplied approval fields, fillout replies, edits, removed fields, aliases, renames, and confirmed deletion now mark the directory pending. Yoko waits 20 seconds after the latest change and then publishes one complete snapshot. Rapid changes are combined, and GitHub writes are serialized to prevent update conflicts.

Yoko updates only `docs/data/characters.json` through GitHub's Contents API. It never runs `git add .` or commits the local working tree, because unrelated source changes may be present. If GitHub is unavailable, the local character operation still succeeds and `/siteadmin status` reports a pending snapshot and the last error. `/siteadmin publish` retries immediately.

Deleting a character will remove it from the current public snapshot. Previous versions can remain in Git history, so only deliberately public roleplay data belongs in the export.

## Updating the site design later

Keep editing `docs/index.html`, `docs/styles.css`, `docs/app.js`, and other site assets on `main` with the bot source. Copy or merge those asset changes into `pages`, then run `/siteadmin publish` again so the live character JSON is refreshed. Do not treat the sample `docs/data/characters.json` on `main` as live server data.
