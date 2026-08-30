# Yoko Character Archive: GitHub Pages setup

The initial scaffold lives in `docs/` so the existing public `YokoBot` repository can publish it directly. It contains only fabricated sample characters. Yoko's ignored `data/*.json` files are not copied or exposed.

## Turn on GitHub Pages

1. Commit and push the new `docs/` scaffold to the repository's `main` branch.
2. Open `https://github.com/alecasone/YokoBot`.
3. Select **Settings**.
4. In the left sidebar, select **Pages**.
5. Under **Build and deployment**, set **Source** to **Deploy from a branch**.
6. Select branch **main** and folder **/docs**, then save.
7. After the first deployment finishes, open `https://alecasone.github.io/YokoBot/`.

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

## Planned publishing path

The current `docs/data/characters.json` is representative sample data. The next bot integration should:

1. Read the canonical local `data/characters.json`.
2. Map it into the sanitized public contract.
3. Write one complete snapshot rather than editing individual records in place.
4. Queue rapid changes and publish them together.
5. Update only `docs/data/characters.json` through GitHub's Contents API.

The GitHub credential should be a fine-grained token restricted to the `YokoBot` repository with **Contents: Read and write**, stored outside the repository as `GITHUB_PAGES_TOKEN`. Yoko must never run `git add .` or commit the whole working tree automatically because unrelated source changes may be present.

Deleting a character will remove it from the current public snapshot. Previous versions can remain in Git history, so only deliberately public roleplay data belongs in the export.
