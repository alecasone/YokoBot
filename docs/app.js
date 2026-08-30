const state = {
  characters: [],
  filtered: []
};

const elements = {
  grid: document.querySelector("#character-grid"),
  status: document.querySelector("#status"),
  search: document.querySelector("#search"),
  region: document.querySelector("#region-filter"),
  sort: document.querySelector("#sort-order"),
  count: document.querySelector("#character-count"),
  regionCount: document.querySelector("#region-count"),
  updated: document.querySelector("#last-updated"),
  dialog: document.querySelector("#character-dialog"),
  dialogTitle: document.querySelector("#dialog-title"),
  dialogAliases: document.querySelector("#dialog-aliases"),
  dialogDetails: document.querySelector("#dialog-details"),
  dialogProperties: document.querySelector("#dialog-properties"),
  dialogReference: document.querySelector("#dialog-reference"),
  dialogRelationships: document.querySelector("#dialog-relationships"),
  dialogClose: document.querySelector("#dialog-close")
};

initialize().catch(error => {
  console.error(error);
  elements.status.textContent = "The archive could not be opened. Try again shortly.";
});

async function initialize() {
  const response = await fetch("./data/characters.json", { cache: "no-store" });
  if (!response.ok) throw new Error(`Character data returned ${response.status}.`);

  const payload = await response.json();
  state.characters = Array.isArray(payload.characters) ? payload.characters.filter(isCharacter) : [];
  elements.count.textContent = state.characters.length.toLocaleString();
  elements.regionCount.textContent = new Set(state.characters.map(item => item.region).filter(Boolean)).size.toLocaleString();
  elements.updated.textContent = formatDate(payload.generatedAt);

  populateRegions();
  elements.search.addEventListener("input", render);
  elements.region.addEventListener("change", render);
  elements.sort.addEventListener("change", render);
  elements.dialogClose.addEventListener("click", () => elements.dialog.close());
  elements.dialog.addEventListener("click", event => {
    if (event.target === elements.dialog) elements.dialog.close();
  });
  elements.dialog.addEventListener("close", clearCharacterQuery);
  window.addEventListener("popstate", openFromQuery);

  render();
  openFromQuery();
}

function render() {
  const query = normalize(elements.search.value);
  const region = elements.region.value;
  state.filtered = state.characters
    .filter(character => !region || character.region === region)
    .filter(character => !query || searchableText(character).includes(query))
    .sort(characterSorter(elements.sort.value));

  elements.grid.replaceChildren(...state.filtered.map(createCard));
  elements.status.textContent = state.filtered.length === 0
    ? "No records match those filters."
    : `${state.filtered.length.toLocaleString()} ${state.filtered.length === 1 ? "record" : "records"} shown.`;
}

function createCard(character, index) {
  const article = document.createElement("article");
  article.className = "character-card";

  const recordNumber = document.createElement("span");
  recordNumber.className = "card-index";
  recordNumber.textContent = `RECORD ${String(index + 1).padStart(3, "0")}`;

  const name = document.createElement("h3");
  name.textContent = character.name;

  const aliases = document.createElement("p");
  aliases.className = "card-aliases";
  aliases.textContent = character.aliases?.length ? `also known as ${character.aliases.join(", ")}` : "";

  const facts = document.createElement("div");
  facts.className = "card-facts";
  addCardFact(facts, "Region", character.region);
  addCardFact(facts, "Occupation", character.occupation);
  addCardFact(facts, "Age", character.age);

  const button = document.createElement("button");
  button.className = "record-button";
  button.type = "button";
  button.textContent = "Open record";
  button.addEventListener("click", () => openCharacter(character, true));

  article.append(recordNumber, name, aliases, facts, button);
  return article;
}

function addCardFact(container, label, value) {
  if (!value) return;
  const row = document.createElement("div");
  row.className = "card-fact";
  const term = document.createElement("span");
  term.textContent = label;
  const description = document.createElement("span");
  description.textContent = value;
  row.append(term, description);
  container.append(row);
}

function openCharacter(character, updateHistory) {
  elements.dialogTitle.textContent = character.name;
  elements.dialogAliases.textContent = character.aliases?.length
    ? `Aliases: ${character.aliases.join(", ")}`
    : "No aliases recorded.";
  elements.dialogDetails.replaceChildren();
  elements.dialogProperties.replaceChildren();

  addDetail(elements.dialogDetails, "Age", character.age);
  addDetail(elements.dialogDetails, "Gender", character.gender);
  addDetail(elements.dialogDetails, "Region", character.region);
  addDetail(elements.dialogDetails, "Occupation", character.occupation);
  for (const [field, value] of Object.entries(character.properties ?? {}))
    addDetail(elements.dialogProperties, humanize(field), displayValue(value));

  const reference = safeWebUrl(character.reference?.value);
  elements.dialogReference.hidden = !reference;
  if (reference) elements.dialogReference.href = reference;
  else elements.dialogReference.removeAttribute("href");
  elements.dialogRelationships.href = `./relationships.html?focus=${encodeURIComponent(character.publicId)}`;

  if (updateHistory) {
    const url = new URL(window.location.href);
    url.searchParams.set("character", character.publicId);
    history.pushState({}, "", url);
  }
  if (!elements.dialog.open) elements.dialog.showModal();
}

function addDetail(container, label, value) {
  if (value === null || value === undefined || value === "") return;
  const row = document.createElement("dl");
  row.className = "record-row";
  const term = document.createElement("dt");
  term.textContent = label;
  const description = document.createElement("dd");
  description.textContent = displayValue(value);
  row.append(term, description);
  container.append(row);
}

function populateRegions() {
  const regions = [...new Set(state.characters.map(character => character.region).filter(Boolean))]
    .sort((left, right) => left.localeCompare(right));
  for (const region of regions) {
    const option = document.createElement("option");
    option.value = region;
    option.textContent = region;
    elements.region.append(option);
  }
}

function openFromQuery() {
  const requested = normalize(new URL(window.location.href).searchParams.get("character") ?? "");
  if (!requested) return;
  const character = state.characters.find(item =>
    normalize(item.publicId) === requested ||
    normalize(item.name) === requested ||
    item.aliases?.some(alias => normalize(alias) === requested));
  if (character) openCharacter(character, false);
}

function clearCharacterQuery() {
  const url = new URL(window.location.href);
  if (!url.searchParams.has("character")) return;
  url.searchParams.delete("character");
  history.pushState({}, "", url);
}

function characterSorter(order) {
  if (order === "region") return (left, right) =>
    (left.region ?? "").localeCompare(right.region ?? "") || left.name.localeCompare(right.name);
  if (order === "newest") return (left, right) =>
    new Date(right.approvedAt ?? 0) - new Date(left.approvedAt ?? 0);
  return (left, right) => left.name.localeCompare(right.name);
}

function searchableText(character) {
  return normalize([
    character.name,
    ...(character.aliases ?? []),
    character.age,
    character.gender,
    character.region,
    character.occupation,
    ...Object.keys(character.properties ?? {}),
    ...Object.values(character.properties ?? {}).map(displayValue)
  ].filter(Boolean).join(" "));
}

function isCharacter(value) {
  return value && typeof value.publicId === "string" && typeof value.name === "string";
}

function normalize(value) {
  return String(value).trim().toLocaleLowerCase();
}

function humanize(value) {
  return String(value).replace(/[-_]+/g, " ").replace(/\b\w/g, letter => letter.toUpperCase());
}

function displayValue(value) {
  if (Array.isArray(value)) return value.map(displayValue).join(", ");
  if (value && typeof value === "object") return JSON.stringify(value);
  return String(value);
}

function safeWebUrl(value) {
  if (!value) return null;
  try {
    const url = new URL(value);
    return url.protocol === "https:" || url.protocol === "http:" ? url.href : null;
  } catch {
    return null;
  }
}

function formatDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.valueOf())
    ? "Unknown"
    : new Intl.DateTimeFormat(undefined, { year: "numeric", month: "short", day: "numeric" }).format(date);
}
