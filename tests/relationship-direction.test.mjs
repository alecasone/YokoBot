import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

class Element {
  constructor() { this.children = []; this.attributes = {}; this.style = { setProperty() {} }; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  setAttribute(key, value) { this.attributes[key] = value; }
  addEventListener() {}
}
const elements = new Map();
const context = { document: {
  querySelector: selector => { if (!elements.has(selector)) elements.set(selector, new Element()); return elements.get(selector); },
  querySelectorAll: () => [], createElement: () => new Element(), createElementNS: () => new Element()
} };
let source = await readFile(new URL("../docs/relationships.js", import.meta.url), "utf8");
source = source.replace(/initialize\(\)\.catch\(error => \{[\s\S]*?\n\}\);/, "");
runInNewContext(source + "\nthis.atlas = { state, buildPairs, relationshipDirection, relationshipText, createConnectionItem, renderGraph };", context);
const { atlas } = context;
const parent = { sourceCharacterId: "godfrey", targetCharacterId: "maribelle", typeId: "biological-parent", displayName: "Biological parent", category: "Biological", isInferred: false };
const child = { ...parent, sourceCharacterId: "maribelle", targetCharacterId: "godfrey", typeId: "biological-child", displayName: "Biological child" };
const pair = atlas.buildPairs([parent, child])[0];
assert.equal(atlas.relationshipDirection(pair), child);
assert.equal(atlas.relationshipDirection({ ...pair, records: [child, parent] }), child);
assert.equal(atlas.relationshipText(child), "Child of");
assert.equal(atlas.relationshipText(parent), "Parent of");
const sibling = { ...child, typeId: "biological-sibling", displayName: "Biological sibling" };
assert.equal(atlas.relationshipDirection({ records: [sibling] }), null);
assert.equal(atlas.relationshipDirection({ records: [parent] }), parent);
atlas.state.charactersById.set("godfrey", { name: "Godfrey" });
const card = atlas.createConnectionItem(child).children[0];
assert.equal(card.children[1].textContent, "Child of");
assert.equal(card.children[2].textContent, "Godfrey");
atlas.state.positions.set("godfrey", { x: 0, y: 0 });
atlas.state.positions.set("maribelle", { x: 0, y: 200 });
atlas.state.graphPairs = [pair];
for (const selected of ["maribelle", "godfrey"]) {
  atlas.state.selectedId = selected;
  atlas.renderGraph();
  const line = elements.get("#map-edges").children[0];
  assert.equal(line.attributes.y1, 200);
  assert.ok(line.attributes.y2 > 27 && line.attributes.y2 < 50);
  assert.equal(line.attributes["marker-end"], "url(#relationship-arrow)");
  assert.equal(elements.get("#map-edge-labels").children[0].textContent, "Child of");
}
console.log("Relationship cards and stable child-to-parent arrows passed.");
