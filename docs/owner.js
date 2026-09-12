// One attribution renderer for cards, character records, and the relationship inspector.
window.CharacterAttribution = (() => {
  function create(owner) {
    const container = document.createElement("div");
    container.className = "character-attribution";
    const byline = document.createElement("span");
    byline.className = "owner-name";
    const name = typeof owner?.displayName === "string" && owner.displayName.trim()
      ? owner.displayName : null;
    byline.textContent = name ? `By ${name}` : "Owner not published";
    container.append(byline);

    // Never coerce numeric snowflakes: JSON numbers may already have lost precision.
    const id = typeof owner?.discordId === "string" && /^[1-9]\d{16,19}$/.test(owner.discordId)
      ? owner.discordId : null;
    if (id) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "owner-copy";
      button.textContent = "⧉ Copy Discord ID";
      button.setAttribute("aria-label", `Copy Discord ID for ${name ?? "this owner"}`);
      const status = document.createElement("span");
      status.className = "owner-copy-status";
      status.setAttribute("role", "status");
      button.addEventListener("click", async event => {
        event.stopPropagation();
        try {
          await navigator.clipboard.writeText(id);
          status.textContent = "Copied!";
        } catch {
          // Only already-public IDs can reach this fallback. Hidden IDs never enter the payload.
          status.textContent = `Copy manually: ${id}`;
        }
      });
      container.append(button, status);
    } else if (owner) {
      const hidden = document.createElement("span");
      hidden.className = "owner-private";
      hidden.textContent = "Discord ID not published";
      container.append(hidden);
    }
    return container;
  }
  return { create };
})();
