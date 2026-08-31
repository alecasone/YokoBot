const SVG_NS = "http://www.w3.org/2000/svg";
const regionPalette = ["#c8a96a", "#8ba6a9", "#a17f9b", "#9ea86b", "#c27f68", "#7892b2", "#b29469"];
const mapColorStorageKey = "yoko.relationshipAtlas.mapColors.v1";
const defaultMapColors = { near: "#ffd65c", far: "#7f1d1d" };
const parentTypeIds = new Set(["biological-parent", "biological-child"]);
const peerTypeIds = new Set([
  "biological-sibling",
  "biological-full-sibling",
  "biological-half-sibling",
  "biological-twin"
]);

const state = {
  characters: [],
  charactersById: new Map(),
  relationships: [],
  relationshipTypes: [],
  focusId: null,
  selectedId: null,
  contextId: null,
  showInferred: true,
  category: "",
  layoutMode: "network",
  positions: new Map(),
  pairs: [],
  graphPairs: [],
  generationRows: [],
  biologicalDistances: new Map(),
  maximumBiologicalDistance: 1,
  mapColors: { ...defaultMapColors },
  pan: { x: 0, y: 0 },
  zoom: 1,
  pointer: null,
  suppressClick: false
};

const elements = {
  characterCount: document.querySelector("#atlas-character-count"),
  connectionCount: document.querySelector("#atlas-connection-count"),
  updated: document.querySelector("#atlas-updated"),
  status: document.querySelector("#atlas-status"),
  search: document.querySelector("#focus-search"),
  searchResults: document.querySelector("#focus-results"),
  layoutButtons: [...document.querySelectorAll("[data-layout]")],
  category: document.querySelector("#category-filter"),
  inferred: document.querySelector("#show-inferred"),
  fit: document.querySelector("#fit-map"),
  zoomIn: document.querySelector("#zoom-in"),
  zoomOut: document.querySelector("#zoom-out"),
  stage: document.querySelector("#map-stage"),
  svg: document.querySelector("#relationship-map"),
  viewport: document.querySelector("#map-viewport"),
  guides: document.querySelector("#map-guides"),
  edges: document.querySelector("#map-edges"),
  edgeLabels: document.querySelector("#map-edge-labels"),
  nodes: document.querySelector("#map-nodes"),
  empty: document.querySelector("#map-empty"),
  inspectorTitle: document.querySelector("#inspector-title"),
  inspectorMeta: document.querySelector("#inspector-meta"),
  inspectorActions: document.querySelector("#inspector-actions"),
  centerSelected: document.querySelector("#center-selected"),
  openRecord: document.querySelector("#open-record"),
  connectionSummary: document.querySelector("#connection-summary"),
  connectionList: document.querySelector("#connection-list"),
  ledgerSummary: document.querySelector("#ledger-summary"),
  relationshipList: document.querySelector("#relationship-list"),
  legendDirect: document.querySelector("#legend-direct"),
  legendInferred: document.querySelector("#legend-inferred"),
  menu: document.querySelector("#node-menu"),
  menuTitle: document.querySelector("#node-menu-title"),
  mapMenu: document.querySelector("#map-menu"),
  mapColorsToggle: document.querySelector("#map-colors-toggle"),
  mapColorPanel: document.querySelector("#map-color-panel"),
  nearColor: document.querySelector("#near-color"),
  farColor: document.querySelector("#far-color"),
  resetMapColors: document.querySelector("#reset-map-colors")
};

initialize().catch(error => {
  console.error(error);
  elements.status.textContent = "The relationship atlas could not be opened. Try again shortly.";
  elements.empty.hidden = false;
});

async function initialize() {
  const response = await fetch("./data/characters.json", { cache: "no-store" });
  if (!response.ok) throw new Error(`Archive data returned ${response.status}.`);

  const payload = await response.json();
  state.characters = Array.isArray(payload.characters)
    ? payload.characters.filter(isCharacter).sort((left, right) => left.name.localeCompare(right.name))
    : [];
  state.charactersById = new Map(state.characters.map(character => [character.publicId, character]));
  state.relationshipTypes = Array.isArray(payload.relationshipTypes) ? payload.relationshipTypes : [];
  state.relationships = Array.isArray(payload.relationships)
    ? payload.relationships.filter(isRelationship).map(normalizeRelationship)
    : [];

  const requestedFocus = new URL(window.location.href).searchParams.get("focus");
  state.focusId = state.charactersById.has(requestedFocus) ? requestedFocus : mostConnectedCharacter();
  state.selectedId = state.focusId;
  elements.characterCount.textContent = state.characters.length.toLocaleString();
  elements.updated.textContent = formatDate(payload.generatedAt);

  loadMapColors();
  populateCategories();
  bindEvents();
  refreshView(true);
  requestAnimationFrame(fitMap);
}

function bindEvents() {
  elements.search.addEventListener("input", () => renderSearchResults(elements.search.value));
  elements.search.addEventListener("keydown", handleSearchKeydown);
  elements.search.addEventListener("focus", () => renderSearchResults(elements.search.value));
  elements.search.addEventListener("blur", () => setTimeout(hideSearchResults, 120));
  for (const button of elements.layoutButtons)
    button.addEventListener("click", () => setLayoutMode(button.dataset.layout));
  elements.category.addEventListener("change", () => {
    state.category = elements.category.value;
    refreshView(true);
    requestAnimationFrame(fitMap);
  });
  elements.inferred.addEventListener("change", () => {
    state.showInferred = elements.inferred.checked;
    refreshView(true);
    requestAnimationFrame(fitMap);
  });
  elements.fit.addEventListener("click", fitMap);
  elements.zoomIn.addEventListener("click", () => zoomAt(1.22));
  elements.zoomOut.addEventListener("click", () => zoomAt(1 / 1.22));
  elements.centerSelected.addEventListener("click", () => setFocus(state.selectedId));
  elements.svg.addEventListener("pointerdown", beginPan);
  elements.svg.addEventListener("wheel", handleWheel, { passive: false });
  elements.svg.addEventListener("keydown", handleMapKeydown);
  window.addEventListener("pointermove", movePointer);
  window.addEventListener("pointerup", endPointer);
  window.addEventListener("resize", debounce(fitMap, 140));
  window.addEventListener("popstate", applyFocusFromUrl);
  document.addEventListener("pointerdown", event => {
    if (!elements.menu.contains(event.target)) hideNodeMenu();
    if (!elements.mapMenu.contains(event.target) && !elements.svg.contains(event.target)) hideMapMenu();
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      hideNodeMenu();
      hideMapMenu();
      hideSearchResults();
    }
  });
  elements.menu.addEventListener("click", handleMenuAction);
  elements.mapColorsToggle.addEventListener("click", toggleMapColors);
  elements.nearColor.addEventListener("input", updateMapColors);
  elements.farColor.addEventListener("input", updateMapColors);
  elements.resetMapColors.addEventListener("click", resetMapColors);
}

function refreshView(relayout) {
  const visible = visibleRelationships();
  state.pairs = buildPairs(visible);
  state.graphPairs = state.layoutMode === "tree" ? buildTreePairs(visible) : state.pairs;
  calculateBiologicalDistances(visible);
  updateLayoutUi();
  if (relayout) layoutGraph(state.graphPairs);
  renderGraph();
  renderInspector(visible);
  renderLedger();

  const heatNotice = visible.some(relationship => relationship.category === "Biological")
    ? ` · heat centered on ${characterName(state.focusId)}`
    : "";
  elements.connectionCount.textContent = state.pairs.length.toLocaleString();
  elements.empty.hidden = state.characters.length !== 0;
  if (state.characters.length === 0) {
    elements.status.textContent = "No public character records have been published yet.";
  } else if (state.relationships.length === 0) {
    elements.status.textContent = "Characters are charted, but no public relationships have been published yet.";
  } else if (state.pairs.length === 0) {
    elements.status.textContent = "No connections match the current filters.";
  } else if (state.layoutMode === "tree") {
    elements.status.textContent = state.graphPairs.length
      ? `${state.positions.size.toLocaleString()} characters arranged from older generations to younger${heatNotice}.`
      : "No direct biological parent, sibling, or twin relationships are available for a family tree.";
  } else {
    const inferredCount = state.pairs.filter(pair => pair.isInferred).length;
    elements.status.textContent = `${state.pairs.length.toLocaleString()} visible ${pluralize(state.pairs.length, "connection")} · ${inferredCount.toLocaleString()} inferred${heatNotice}.`;
  }
}

function visibleRelationships() {
  return state.relationships.filter(relationship =>
    (!state.category || relationship.category === state.category) &&
    (state.showInferred || !relationship.isInferred));
}

function calculateBiologicalDistances(visibleRelationships) {
  const adjacency = new Map(state.characters.map(character => [character.publicId, new Set()]));
  const biologicalPairs = buildPairs(visibleRelationships.filter(relationship =>
    relationship.category === "Biological"));
  for (const pair of biologicalPairs) {
    adjacency.get(pair.a)?.add(pair.b);
    adjacency.get(pair.b)?.add(pair.a);
  }

  const distances = new Map();
  if (state.charactersById.has(state.focusId)) distances.set(state.focusId, 0);
  const queue = state.focusId ? [state.focusId] : [];
  for (let index = 0; index < queue.length; index += 1) {
    const current = queue[index];
    for (const neighbor of adjacency.get(current) ?? []) {
      if (distances.has(neighbor)) continue;
      distances.set(neighbor, distances.get(current) + 1);
      queue.push(neighbor);
    }
  }

  state.biologicalDistances = distances;
  state.maximumBiologicalDistance = Math.max(1, ...distances.values());
}

function buildPairs(relationships) {
  const pairs = new Map();
  for (const relationship of relationships) {
    const ids = [relationship.sourceCharacterId, relationship.targetCharacterId].sort();
    const key = ids.join("|");
    if (!pairs.has(key)) pairs.set(key, { key, a: ids[0], b: ids[1], records: [] });
    pairs.get(key).records.push(relationship);
  }
  return [...pairs.values()].map(pair => ({
    ...pair,
    isInferred: pair.records.every(record => record.isInferred)
  }));
}

function buildTreePairs(relationships) {
  return buildPairs(relationships.filter(relationship =>
    relationship.category === "Biological" &&
    !relationship.isInferred &&
    (parentTypeIds.has(relationship.typeId) || peerTypeIds.has(relationship.typeId))))
    .map(pair => ({ ...pair, treeKind: parentChildForPair(pair) ? "parent" : "peer" }));
}

function layoutGraph(pairs) {
  state.positions = new Map();
  state.generationRows = [];
  if (state.characters.length === 0) return;

  if (state.layoutMode === "tree") {
    layoutFamilyTree(pairs);
    return;
  }

  const focusId = state.charactersById.has(state.focusId) ? state.focusId : state.characters[0].publicId;
  state.focusId = focusId;
  const adjacency = new Map(state.characters.map(character => [character.publicId, new Set()]));
  for (const pair of pairs) {
    adjacency.get(pair.a)?.add(pair.b);
    adjacency.get(pair.b)?.add(pair.a);
  }

  const distance = new Map([[focusId, 0]]);
  const queue = [focusId];
  for (let index = 0; index < queue.length; index += 1) {
    const current = queue[index];
    for (const neighbor of adjacency.get(current) ?? []) {
      if (distance.has(neighbor)) continue;
      distance.set(neighbor, distance.get(current) + 1);
      queue.push(neighbor);
    }
  }

  state.positions.set(focusId, { x: 0, y: 0 });
  const maxDistance = Math.max(0, ...distance.values());
  for (let level = 1; level <= maxDistance; level += 1) {
    const ids = state.characters
      .filter(character => distance.get(character.publicId) === level)
      .map(character => character.publicId);
    placeAcrossRings(ids, 235 + (level - 1) * 205, level * 0.47);
  }

  const disconnected = state.characters
    .filter(character => !distance.has(character.publicId))
    .map(character => character.publicId);
  placeAcrossRings(disconnected, Math.max(490, 255 + maxDistance * 220), 0.18);
}

function placeAcrossRings(ids, firstRadius, offset) {
  const perRing = 12;
  for (let start = 0; start < ids.length; start += perRing) {
    const ring = ids.slice(start, start + perRing);
    const radius = firstRadius + Math.floor(start / perRing) * 180;
    ring.forEach((id, index) => {
      const angle = offset - Math.PI / 2 + (index * Math.PI * 2) / ring.length;
      state.positions.set(id, { x: Math.cos(angle) * radius, y: Math.sin(angle) * radius });
    });
  }
}

function layoutFamilyTree(pairs) {
  const nodeIds = new Set(pairs.flatMap(pair => [pair.a, pair.b]));
  if (nodeIds.size === 0 && state.charactersById.has(state.selectedId)) nodeIds.add(state.selectedId);
  if (nodeIds.size === 0) return;

  const parents = new Map([...nodeIds].map(id => [id, id]));
  const find = id => {
    let root = id;
    while (parents.get(root) !== root) root = parents.get(root);
    while (parents.get(id) !== id) {
      const next = parents.get(id);
      parents.set(id, root);
      id = next;
    }
    return root;
  };
  const union = (left, right) => {
    const leftRoot = find(left);
    const rightRoot = find(right);
    if (leftRoot !== rightRoot) parents.set(rightRoot, leftRoot);
  };

  for (const pair of pairs.filter(pair => pair.treeKind === "peer")) union(pair.a, pair.b);

  const groups = new Map();
  for (const id of nodeIds) {
    const root = find(id);
    if (!groups.has(root)) groups.set(root, []);
    groups.get(root).push(id);
  }

  const groupEdges = new Map();
  for (const pair of pairs) {
    const relation = parentChildForPair(pair);
    if (!relation) continue;
    const parentGroup = find(relation.parent);
    const childGroup = find(relation.child);
    if (parentGroup === childGroup) continue;
    groupEdges.set(`${parentGroup}|${childGroup}`, { parent: parentGroup, child: childGroup });
  }

  const outgoing = new Map([...groups.keys()].map(id => [id, new Set()]));
  const incoming = new Map([...groups.keys()].map(id => [id, 0]));
  for (const edge of groupEdges.values()) {
    if (outgoing.get(edge.parent).has(edge.child)) continue;
    outgoing.get(edge.parent).add(edge.child);
    incoming.set(edge.child, incoming.get(edge.child) + 1);
  }

  const rank = new Map([...groups.keys()].map(id => [id, 0]));
  const queue = [...groups.keys()]
    .filter(id => incoming.get(id) === 0)
    .sort((left, right) => compareAgeValues(oldestAge(groups.get(left)), oldestAge(groups.get(right))));
  for (let index = 0; index < queue.length; index += 1) {
    const current = queue[index];
    for (const child of outgoing.get(current)) {
      rank.set(child, Math.max(rank.get(child), rank.get(current) + 1));
      incoming.set(child, incoming.get(child) - 1);
      if (incoming.get(child) === 0) queue.push(child);
    }
  }

  const rows = new Map();
  for (const [groupId, ids] of groups) {
    const row = rank.get(groupId) ?? 0;
    if (!rows.has(row)) rows.set(row, []);
    rows.get(row).push({ ids, oldest: oldestAge(ids) });
  }

  const horizontalGap = 265;
  const verticalGap = 235;
  for (const [row, rowGroups] of [...rows].sort(([left], [right]) => left - right)) {
    const ids = rowGroups
      .sort((left, right) => compareAgeValues(left.oldest, right.oldest) || characterName(left.ids[0]).localeCompare(characterName(right.ids[0])))
      .flatMap(group => group.ids.sort(compareCharactersOldestFirst));
    ids.forEach((id, index) => state.positions.set(id, {
      x: (index - (ids.length - 1) / 2) * horizontalGap,
      y: row * verticalGap
    }));
    state.generationRows.push({ rank: row, y: row * verticalGap, ids });
  }
}

function parentChildForPair(pair) {
  for (const record of pair.records.filter(record => !record.isInferred)) {
    if (record.typeId === "biological-parent")
      return { parent: record.sourceCharacterId, child: record.targetCharacterId };
    if (record.typeId === "biological-child")
      return { parent: record.targetCharacterId, child: record.sourceCharacterId };
  }
  return null;
}

function renderGraph() {
  elements.guides.replaceChildren();
  elements.edges.replaceChildren();
  elements.edgeLabels.replaceChildren();
  elements.nodes.replaceChildren();

  renderGenerationGuides();
  for (const pair of state.graphPairs) {
    const source = state.positions.get(pair.a);
    const target = state.positions.get(pair.b);
    if (!source || !target) continue;
    const selected = pair.a === state.selectedId || pair.b === state.selectedId;
    const focused = pair.a === state.focusId || pair.b === state.focusId;
    const relationshipClass = state.layoutMode === "tree"
      ? pair.treeKind === "parent" ? "map-edge--tree-parent" : "map-edge--tree-peer"
      : pair.isInferred ? "map-edge--inferred" : "map-edge--direct";
    const attributes = {
      x1: source.x,
      y1: source.y,
      x2: target.x,
      y2: target.y,
      class: `map-edge ${relationshipClass}${selected ? " is-selected" : ""}${focused ? " is-focused" : ""}`
    };
    const heatColor = heatColorForPair(pair);
    if (heatColor) attributes.style = `--heat-color: ${heatColor}`;
    const line = svgElement("line", attributes);
    elements.edges.append(line);

    if (selected) {
      const labelPoint = edgeLabelPoint(pair, source, target);
      const label = svgElement("text", {
        x: labelPoint.x,
        y: labelPoint.y,
        class: "map-edge-label",
        "text-anchor": "middle"
      });
      label.textContent = relationshipLabel(pair, state.selectedId);
      elements.edgeLabels.append(label);
    }
  }

  for (const character of state.characters) {
    const position = state.positions.get(character.publicId);
    if (!position) continue;
    elements.nodes.append(createNode(character, position));
  }
  applyTransform();
}

function renderGenerationGuides() {
  if (state.layoutMode !== "tree" || state.generationRows.length === 0) return;
  const points = [...state.positions.values()];
  const minX = Math.min(...points.map(point => point.x)) - 110;
  const maxX = Math.max(...points.map(point => point.x)) + 110;
  const lastIndex = state.generationRows.length - 1;
  state.generationRows.forEach((row, index) => {
    const guideY = row.y - 92;
    const line = svgElement("line", { x1: minX, y1: guideY, x2: maxX, y2: guideY, class: "generation-guide" });
    const label = svgElement("text", { x: minX, y: guideY - 12, class: "generation-label" });
    const direction = index === 0 ? " · older" : index === lastIndex ? " · younger" : "";
    label.textContent = `GENERATION ${row.rank + 1}${direction}`;
    elements.guides.append(line, label);
  });
}

function edgeLabelPoint(pair, source, target) {
  const selectedIsSource = pair.a === state.selectedId;
  const origin = selectedIsSource ? source : target;
  const destination = selectedIsSource ? target : source;
  const ratio = state.layoutMode === "tree" ? 0.5 : 0.64;
  const dx = destination.x - origin.x;
  const dy = destination.y - origin.y;
  const length = Math.max(1, Math.hypot(dx, dy));
  const side = stableHash(pair.key) % 2 === 0 ? 1 : -1;
  return {
    x: origin.x + dx * ratio + (-dy / length) * 12 * side,
    y: origin.y + dy * ratio + (dx / length) * 12 * side - 8
  };
}

function createNode(character, position) {
  const selected = character.publicId === state.selectedId;
  const focused = character.publicId === state.focusId;
  const group = svgElement("g", {
    class: `map-node${selected ? " is-selected" : ""}${focused ? " is-focused" : ""}`,
    transform: `translate(${position.x} ${position.y})`,
    tabindex: "0",
    role: "button",
    "aria-label": `${character.name}. Select to inspect; right-click for map actions.`,
    "data-character-id": character.publicId,
    style: `--node-color: ${colorForRegion(character.region)}`
  });
  const circle = svgElement("circle", { r: focused ? 35 : selected ? 31 : 27 });
  const initials = svgElement("text", { class: "map-node-initials", "text-anchor": "middle", y: "5" });
  initials.textContent = initialsFor(character.name);
  const label = svgElement("text", { class: "map-node-name", "text-anchor": "middle", y: focused ? "55" : "48" });
  label.textContent = truncate(character.name, 22);
  const title = svgElement("title");
  title.textContent = `${character.name}${character.region ? ` · ${character.region}` : ""}`;
  group.append(circle, initials, label, title);

  group.addEventListener("pointerdown", event => beginNodeDrag(event, character.publicId));
  group.addEventListener("click", event => {
    event.stopPropagation();
    if (state.suppressClick) return;
    selectCharacter(character.publicId);
  });
  group.addEventListener("dblclick", event => {
    event.preventDefault();
    event.stopPropagation();
    setFocus(character.publicId);
  });
  group.addEventListener("contextmenu", event => {
    event.preventDefault();
    event.stopPropagation();
    selectCharacter(character.publicId);
    showNodeMenu(character.publicId, event.clientX, event.clientY);
  });
  group.addEventListener("keydown", event => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      selectCharacter(character.publicId);
    }
    if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) {
      event.preventDefault();
      const rect = elements.stage.getBoundingClientRect();
      showNodeMenu(character.publicId, rect.left + rect.width / 2, rect.top + rect.height / 2);
    }
  });
  return group;
}

function renderInspector(visible) {
  const character = state.charactersById.get(state.selectedId);
  if (!character) {
    elements.inspectorTitle.textContent = "Choose a character";
    elements.inspectorMeta.textContent = "Select a node to read its connections.";
    elements.inspectorActions.hidden = true;
    elements.connectionSummary.textContent = "";
    elements.connectionList.replaceChildren();
    return;
  }

  elements.inspectorTitle.textContent = character.name;
  elements.inspectorMeta.textContent = [character.region, character.occupation].filter(Boolean).join(" · ") || "Public character record";
  elements.inspectorActions.hidden = false;
  elements.openRecord.href = `./?character=${encodeURIComponent(character.publicId)}`;

  const connections = visible
    .filter(relationship => relationship.sourceCharacterId === character.publicId)
    .filter(uniqueRelationship)
    .sort((left, right) => Number(left.isInferred) - Number(right.isInferred) ||
      left.displayName.localeCompare(right.displayName) ||
      characterName(left.targetCharacterId).localeCompare(characterName(right.targetCharacterId)));
  const directCount = connections.filter(connection => !connection.isInferred).length;
  const inferredCount = connections.length - directCount;
  elements.connectionSummary.textContent = connections.length
    ? `${directCount} direct · ${inferredCount} inferred`
    : "No visible connections for the current filters.";
  elements.connectionList.replaceChildren(...connections.map(createConnectionItem));
}

function createConnectionItem(connection) {
  const target = state.charactersById.get(connection.targetCharacterId);
  const item = document.createElement("li");
  const button = document.createElement("button");
  button.type = "button";
  button.className = "connection-card";
  button.addEventListener("click", () => selectCharacter(connection.targetCharacterId));

  const kind = document.createElement("span");
  kind.className = `connection-kind ${connection.isInferred ? "is-inferred" : "is-direct"}`;
  kind.textContent = connection.isInferred ? "Inferred" : "Direct";
  const relation = document.createElement("strong");
  relation.textContent = connection.displayName;
  const name = document.createElement("span");
  name.className = "connection-target";
  name.textContent = target?.name ?? "Unknown character";
  button.append(kind, relation, name);
  if (connection.isInferred && connection.explanation) {
    const explanation = document.createElement("small");
    explanation.textContent = `Because ${connection.explanation}.`;
    button.append(explanation);
  }
  item.append(button);
  return item;
}

function renderLedger() {
  const pairs = [...state.pairs].sort((left, right) =>
    characterName(left.a).localeCompare(characterName(right.a)) ||
    characterName(left.b).localeCompare(characterName(right.b)));
  elements.ledgerSummary.textContent = `${pairs.length.toLocaleString()} ${pluralize(pairs.length, "thread")}`;
  if (pairs.length === 0) {
    const empty = document.createElement("p");
    empty.className = "ledger-empty";
    empty.textContent = "No connections match these filters.";
    elements.relationshipList.replaceChildren(empty);
    return;
  }
  elements.relationshipList.replaceChildren(...pairs.map(createLedgerRow));
}

function createLedgerRow(pair) {
  const row = document.createElement("article");
  row.className = "relationship-row";
  const source = state.charactersById.get(pair.a);
  const target = state.charactersById.get(pair.b);
  const perspective = dedupeBy(pair.records.filter(record => record.sourceCharacterId === pair.a), record => record.typeId);

  const names = document.createElement("button");
  names.type = "button";
  names.className = "relationship-row__names";
  names.textContent = `${source?.name ?? "Unknown"}  ↔  ${target?.name ?? "Unknown"}`;
  names.addEventListener("click", () => setFocus(pair.a));

  const labels = document.createElement("div");
  labels.className = "relationship-row__labels";
  for (const record of perspective) {
    const badge = document.createElement("span");
    badge.className = record.isInferred ? "relation-badge is-inferred" : "relation-badge is-direct";
    badge.textContent = record.displayName;
    labels.append(badge);
  }

  const evidence = document.createElement("span");
  evidence.className = `relationship-row__evidence ${pair.isInferred ? "is-inferred" : "is-direct"}`;
  evidence.textContent = pair.isInferred ? "Inferred" : "Approved";
  row.append(names, labels, evidence);
  return row;
}

function renderSearchResults(query) {
  const normalized = normalize(query);
  const results = state.characters
    .filter(character => !normalized || searchableName(character).includes(normalized))
    .slice(0, 8);
  if (results.length === 0 || (!normalized && document.activeElement !== elements.search)) {
    hideSearchResults();
    return;
  }
  elements.searchResults.replaceChildren(...results.map(character => {
    const button = document.createElement("button");
    button.type = "button";
    button.role = "option";
    const name = document.createElement("strong");
    name.textContent = character.name;
    const meta = document.createElement("span");
    meta.textContent = [character.region, ...(character.aliases ?? [])].filter(Boolean).join(" · ");
    button.append(name, meta);
    button.addEventListener("pointerdown", event => event.preventDefault());
    button.addEventListener("click", () => chooseSearchResult(character));
    return button;
  }));
  elements.searchResults.hidden = false;
  elements.search.setAttribute("aria-expanded", "true");
}

function handleSearchKeydown(event) {
  if (event.key === "Enter") {
    const first = elements.searchResults.querySelector("button");
    if (first) {
      event.preventDefault();
      first.click();
    }
  } else if (event.key === "Escape") {
    hideSearchResults();
  }
}

function chooseSearchResult(character) {
  elements.search.value = character.name;
  hideSearchResults();
  setFocus(character.publicId);
}

function hideSearchResults() {
  elements.searchResults.hidden = true;
  elements.search.setAttribute("aria-expanded", "false");
}

function setLayoutMode(layout) {
  if (layout !== "network" && layout !== "tree") return;
  state.layoutMode = layout;
  if (layout === "tree" && [...elements.category.options].some(option => option.value === "Biological")) {
    state.category = "Biological";
    elements.category.value = "Biological";
  }
  refreshView(true);
  requestAnimationFrame(fitMap);
}

function updateLayoutUi() {
  const isTree = state.layoutMode === "tree";
  for (const button of elements.layoutButtons)
    button.setAttribute("aria-pressed", String(button.dataset.layout === state.layoutMode));
  elements.category.disabled = isTree;
  elements.stage.classList.toggle("is-tree", isTree);
  elements.legendDirect.textContent = isTree ? "Parent → child" : "Direct";
  elements.legendInferred.textContent = isTree ? "Sibling / twin" : "Inferred";
}

function setFocus(characterId, updateHistory = true) {
  if (!state.charactersById.has(characterId)) return;
  if (state.layoutMode === "tree") {
    const treeIds = new Set(buildTreePairs(visibleRelationships()).flatMap(pair => [pair.a, pair.b]));
    if (!treeIds.has(characterId)) state.layoutMode = "network";
  }
  state.focusId = characterId;
  state.selectedId = characterId;
  if (updateHistory) writeFocusToUrl(characterId);
  refreshView(true);
  requestAnimationFrame(fitMap);
}

function selectCharacter(characterId) {
  if (!state.charactersById.has(characterId)) return;
  state.selectedId = characterId;
  renderGraph();
  renderInspector(visibleRelationships());
}

function writeFocusToUrl(characterId) {
  const url = new URL(window.location.href);
  url.searchParams.set("focus", characterId);
  history.pushState({}, "", url);
}

function applyFocusFromUrl() {
  const characterId = new URL(window.location.href).searchParams.get("focus");
  if (state.charactersById.has(characterId)) setFocus(characterId, false);
}

function beginPan(event) {
  if (event.button !== 0 || event.target.closest(".map-node")) return;
  hideNodeMenu();
  hideMapMenu();
  state.pointer = {
    kind: "pan",
    id: event.pointerId,
    startX: event.clientX,
    startY: event.clientY,
    panX: state.pan.x,
    panY: state.pan.y,
    moved: false
  };
  elements.stage.classList.add("is-panning");
}

function beginNodeDrag(event, characterId) {
  if (event.button !== 0) return;
  event.stopPropagation();
  hideMapMenu();
  const position = state.positions.get(characterId);
  if (!position) return;
  state.pointer = {
    kind: "node",
    id: event.pointerId,
    characterId,
    startX: event.clientX,
    startY: event.clientY,
    nodeX: position.x,
    nodeY: position.y,
    moved: false
  };
  state.suppressClick = false;
}

function movePointer(event) {
  if (!state.pointer || state.pointer.id !== event.pointerId) return;
  const dx = event.clientX - state.pointer.startX;
  const dy = event.clientY - state.pointer.startY;
  if (state.pointer.kind === "pan") {
    if (Math.hypot(dx, dy) > 4) state.pointer.moved = true;
    state.pan.x = state.pointer.panX + dx;
    state.pan.y = state.pointer.panY + dy;
    applyTransform();
    return;
  }

  if (Math.hypot(dx, dy) > 3) state.pointer.moved = true;
  state.positions.set(state.pointer.characterId, {
    x: state.pointer.nodeX + dx / state.zoom,
    y: state.pointer.nodeY + dy / state.zoom
  });
  renderGraph();
}

function endPointer(event) {
  if (!state.pointer || state.pointer.id !== event.pointerId) return;
  const pointer = state.pointer;
  if (state.pointer.kind === "node" && state.pointer.moved) {
    state.suppressClick = true;
    setTimeout(() => { state.suppressClick = false; }, 0);
  }
  state.pointer = null;
  elements.stage.classList.remove("is-panning");
  if (pointer.kind === "pan" && !pointer.moved)
    showMapMenu(event.clientX, event.clientY);
}

function handleWheel(event) {
  event.preventDefault();
  const rect = elements.svg.getBoundingClientRect();
  zoomAt(Math.exp(-event.deltaY * 0.0015), event.clientX - rect.left, event.clientY - rect.top);
}

function handleMapKeydown(event) {
  if (event.key === "+" || event.key === "=") {
    event.preventDefault();
    zoomAt(1.2);
  } else if (event.key === "-") {
    event.preventDefault();
    zoomAt(1 / 1.2);
  } else if (event.key === "0") {
    event.preventDefault();
    fitMap();
  } else if (event.key === "Enter") {
    event.preventDefault();
    const rect = elements.svg.getBoundingClientRect();
    showMapMenu(rect.left + rect.width / 2, rect.top + rect.height / 2);
  }
}

function zoomAt(factor, screenX, screenY) {
  const rect = elements.svg.getBoundingClientRect();
  const anchorX = screenX ?? rect.width / 2;
  const anchorY = screenY ?? rect.height / 2;
  const previous = state.zoom;
  const next = clamp(previous * factor, 0.22, 2.8);
  const worldX = (anchorX - state.pan.x) / previous;
  const worldY = (anchorY - state.pan.y) / previous;
  state.zoom = next;
  state.pan.x = anchorX - worldX * next;
  state.pan.y = anchorY - worldY * next;
  applyTransform();
}

function fitMap() {
  const rect = elements.svg.getBoundingClientRect();
  if (!rect.width || !rect.height || state.positions.size === 0) return;
  const points = [...state.positions.values()];
  const horizontalMargin = state.layoutMode === "tree" ? 155 : 70;
  const topMargin = state.layoutMode === "tree" ? 140 : 80;
  const minX = Math.min(...points.map(point => point.x)) - horizontalMargin;
  const maxX = Math.max(...points.map(point => point.x)) + horizontalMargin;
  const minY = Math.min(...points.map(point => point.y)) - topMargin;
  const maxY = Math.max(...points.map(point => point.y)) + 80;
  const width = Math.max(160, maxX - minX);
  const height = Math.max(160, maxY - minY);
  state.zoom = clamp(Math.min((rect.width - 42) / width, (rect.height - 42) / height), 0.22, 1.35);
  state.pan.x = rect.width / 2 - ((minX + maxX) / 2) * state.zoom;
  state.pan.y = rect.height / 2 - ((minY + maxY) / 2) * state.zoom;
  applyTransform();
}

function applyTransform() {
  elements.viewport.setAttribute("transform", `translate(${state.pan.x} ${state.pan.y}) scale(${state.zoom})`);
}

function showNodeMenu(characterId, clientX, clientY) {
  const character = state.charactersById.get(characterId);
  if (!character) return;
  hideMapMenu();
  state.contextId = characterId;
  elements.menuTitle.textContent = character.name;
  elements.menu.hidden = false;
  requestAnimationFrame(() => {
    const rect = elements.menu.getBoundingClientRect();
    elements.menu.style.left = `${clamp(clientX, 8, window.innerWidth - rect.width - 8)}px`;
    elements.menu.style.top = `${clamp(clientY, 8, window.innerHeight - rect.height - 8)}px`;
    elements.menu.querySelector("button")?.focus();
  });
}

function hideNodeMenu() {
  elements.menu.hidden = true;
  state.contextId = null;
}

function showMapMenu(clientX, clientY) {
  hideNodeMenu();
  elements.mapColorPanel.hidden = true;
  elements.mapColorsToggle.setAttribute("aria-expanded", "false");
  elements.mapMenu.hidden = false;
  requestAnimationFrame(() => {
    const rect = elements.mapMenu.getBoundingClientRect();
    elements.mapMenu.style.left = `${clamp(clientX, 8, window.innerWidth - rect.width - 8)}px`;
    elements.mapMenu.style.top = `${clamp(clientY, 8, window.innerHeight - rect.height - 8)}px`;
    elements.mapColorsToggle.focus();
  });
}

function hideMapMenu() {
  elements.mapMenu.hidden = true;
  elements.mapColorPanel.hidden = true;
  elements.mapColorsToggle.setAttribute("aria-expanded", "false");
}

function toggleMapColors() {
  const opening = elements.mapColorPanel.hidden;
  elements.mapColorPanel.hidden = !opening;
  elements.mapColorsToggle.setAttribute("aria-expanded", String(opening));
  if (opening) {
    requestAnimationFrame(() => {
      const rect = elements.mapMenu.getBoundingClientRect();
      if (rect.bottom > window.innerHeight - 8)
        elements.mapMenu.style.top = `${Math.max(8, window.innerHeight - rect.height - 8)}px`;
      elements.nearColor.focus();
    });
  }
}

function loadMapColors() {
  try {
    const saved = JSON.parse(window.localStorage.getItem(mapColorStorageKey) ?? "null");
    if (isHexColor(saved?.near) && isHexColor(saved?.far))
      state.mapColors = { near: saved.near.toLowerCase(), far: saved.far.toLowerCase() };
  } catch { }
  applyMapColors();
}

function updateMapColors() {
  if (!isHexColor(elements.nearColor.value) || !isHexColor(elements.farColor.value)) return;
  state.mapColors = {
    near: elements.nearColor.value.toLowerCase(),
    far: elements.farColor.value.toLowerCase()
  };
  applyMapColors();
  saveMapColors();
  renderGraph();
}

function resetMapColors() {
  state.mapColors = { ...defaultMapColors };
  applyMapColors();
  saveMapColors();
  renderGraph();
}

function applyMapColors() {
  elements.nearColor.value = state.mapColors.near;
  elements.farColor.value = state.mapColors.far;
  document.documentElement.style.setProperty("--heat-near", state.mapColors.near);
  document.documentElement.style.setProperty("--heat-far", state.mapColors.far);
}

function saveMapColors() {
  try { window.localStorage.setItem(mapColorStorageKey, JSON.stringify(state.mapColors)); }
  catch { }
}

async function handleMenuAction(event) {
  const action = event.target.closest("button")?.dataset.action;
  const characterId = state.contextId;
  if (!action || !state.charactersById.has(characterId)) return;
  if (action === "center") {
    setFocus(characterId);
  } else if (action === "record") {
    window.location.href = `./?character=${encodeURIComponent(characterId)}`;
  } else if (action === "copy") {
    const url = new URL(window.location.href);
    url.searchParams.set("focus", characterId);
    try {
      await navigator.clipboard.writeText(url.href);
      elements.status.textContent = `Copied a focused link for ${characterName(characterId)}.`;
    } catch {
      elements.status.textContent = "The browser could not copy the link; use the address bar instead.";
    }
  }
  hideNodeMenu();
}

function populateCategories() {
  const categories = [...new Set([
    ...state.relationshipTypes.map(type => type.category),
    ...state.relationships.map(relationship => relationship.category)
  ].filter(Boolean))].sort((left, right) => left.localeCompare(right));
  for (const category of categories) {
    const option = document.createElement("option");
    option.value = category;
    option.textContent = category;
    elements.category.append(option);
  }
}

function mostConnectedCharacter() {
  if (state.characters.length === 0) return null;
  const counts = new Map(state.characters.map(character => [character.publicId, 0]));
  for (const relationship of state.relationships) {
    counts.set(relationship.sourceCharacterId, (counts.get(relationship.sourceCharacterId) ?? 0) + 1);
  }
  return [...state.characters].sort((left, right) =>
    (counts.get(right.publicId) ?? 0) - (counts.get(left.publicId) ?? 0) ||
    left.name.localeCompare(right.name))[0].publicId;
}

function relationshipLabel(pair, perspectiveId) {
  const records = dedupeBy(
    pair.records.filter(record => record.sourceCharacterId === perspectiveId),
    record => record.typeId);
  const labels = records.map(record => record.displayName.replace(/^Biological /i, ""));
  if (labels.length === 0) return pair.isInferred ? "Inferred tie" : "Direct tie";
  if (labels.length <= 2) return labels.join(" · ");
  return `${labels.slice(0, 2).join(" · ")} +${labels.length - 2}`;
}

function uniqueRelationship(value, index, array) {
  return array.findIndex(candidate =>
    candidate.targetCharacterId === value.targetCharacterId &&
    candidate.typeId === value.typeId) === index;
}

function normalizeRelationship(value) {
  const type = state.relationshipTypes.find(candidate => candidate.id === value.typeId);
  return {
    sourceCharacterId: value.sourceCharacterId,
    targetCharacterId: value.targetCharacterId,
    typeId: value.typeId,
    displayName: value.displayName || type?.displayName || humanize(value.typeId),
    category: value.category || type?.category || "Other",
    isInferred: Boolean(value.isInferred),
    explanation: value.explanation || null
  };
}

function isCharacter(value) {
  return value && typeof value.publicId === "string" && typeof value.name === "string";
}

function isRelationship(value) {
  return value &&
    typeof value.sourceCharacterId === "string" &&
    typeof value.targetCharacterId === "string" &&
    typeof value.typeId === "string" &&
    state.charactersById.has(value.sourceCharacterId) &&
    state.charactersById.has(value.targetCharacterId) &&
    value.sourceCharacterId !== value.targetCharacterId;
}

function searchableName(character) {
  return normalize([character.name, ...(character.aliases ?? [])].join(" "));
}

function characterName(characterId) {
  return state.charactersById.get(characterId)?.name ?? "Unknown character";
}

function oldestAge(characterIds) {
  return Math.max(...characterIds.map(characterAge));
}

function characterAge(characterId) {
  const match = String(state.charactersById.get(characterId)?.age ?? "").match(/\d+(?:\.\d+)?/);
  return match ? Number(match[0]) : Number.NEGATIVE_INFINITY;
}

function compareCharactersOldestFirst(leftId, rightId) {
  return compareAgeValues(characterAge(leftId), characterAge(rightId)) ||
    characterName(leftId).localeCompare(characterName(rightId));
}

function compareAgeValues(left, right) {
  if (left === right) return 0;
  if (!Number.isFinite(left)) return 1;
  if (!Number.isFinite(right)) return -1;
  return right - left;
}

function stableHash(value) {
  let hash = 0;
  for (const character of value) hash = ((hash << 5) - hash + character.charCodeAt(0)) | 0;
  return Math.abs(hash);
}

function heatColorForPair(pair) {
  if (!pair.records.some(record => record.category === "Biological")) return null;
  const distances = [
    state.biologicalDistances.get(pair.a),
    state.biologicalDistances.get(pair.b)
  ].filter(Number.isFinite);
  if (distances.length === 0) return state.mapColors.far;
  const distance = Math.max(...distances);
  const progress = state.maximumBiologicalDistance <= 1
    ? 0
    : clamp((Math.max(1, distance) - 1) / (state.maximumBiologicalDistance - 1), 0, 1);
  return mixHexColors(state.mapColors.near, state.mapColors.far, progress);
}

function mixHexColors(start, end, progress) {
  const startChannels = hexChannels(start);
  const endChannels = hexChannels(end);
  const channels = startChannels.map((channel, index) =>
    Math.round(channel + (endChannels[index] - channel) * progress));
  return `#${channels.map(channel => channel.toString(16).padStart(2, "0")).join("")}`;
}

function hexChannels(value) {
  return [1, 3, 5].map(index => Number.parseInt(value.slice(index, index + 2), 16));
}

function isHexColor(value) {
  return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value);
}

function colorForRegion(region) {
  const text = region || "unknown";
  let hash = 0;
  for (const character of text) hash = ((hash << 5) - hash + character.charCodeAt(0)) | 0;
  return regionPalette[Math.abs(hash) % regionPalette.length];
}

function initialsFor(name) {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]).join("").toLocaleUpperCase();
}

function svgElement(name, attributes = {}) {
  const element = document.createElementNS(SVG_NS, name);
  for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, value);
  return element;
}

function dedupeBy(values, keySelector) {
  const seen = new Set();
  return values.filter(value => {
    const key = keySelector(value);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function normalize(value) {
  return String(value).trim().toLocaleLowerCase();
}

function humanize(value) {
  return String(value).replace(/[-_]+/g, " ").replace(/\b\w/g, letter => letter.toUpperCase());
}

function truncate(value, length) {
  return value.length > length ? `${value.slice(0, length - 1)}…` : value;
}

function pluralize(count, noun) {
  return count === 1 ? noun : `${noun}s`;
}

function clamp(value, minimum, maximum) {
  return Math.min(maximum, Math.max(minimum, value));
}

function debounce(callback, delay) {
  let timeout;
  return (...arguments_) => {
    clearTimeout(timeout);
    timeout = setTimeout(() => callback(...arguments_), delay);
  };
}

function formatDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.valueOf())
    ? "Unknown"
    : new Intl.DateTimeFormat(undefined, { year: "numeric", month: "short", day: "numeric" }).format(date);
}
