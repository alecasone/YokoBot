import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

class Element {
  constructor() { this.children = []; this.attributes = {}; this.style = { setProperty() {} }; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  setAttribute(key, value) { this.attributes[key] = value; }
  getAttribute(key) { return this.attributes[key]; }
  getBBox() { return { x: Number(this.attributes.x ?? 0), y: Number(this.attributes.y ?? 0), width: 0, height: 0 }; }
  addEventListener() {}
}
const elements = new Map();
const context = { Option: class extends Element {
  constructor(text, value) { super(); this.textContent = text; this.value = value; }
}, document: {
  querySelector: selector => { if (!elements.has(selector)) elements.set(selector, new Element()); return elements.get(selector); },
  querySelectorAll: () => [], createElement: () => new Element(), createElementNS: () => new Element()
} };
let source = await readFile(new URL("../docs/relationships.js", import.meta.url), "utf8");
source = source.replace(/initialize\(\)\.catch\(error => \{[\s\S]*?\n\}\);/, "");
runInNewContext(source + "\nthis.atlas = { state, buildPairs, buildTreePairs, relationshipDirection, relationshipText, createConnectionItem, renderGraph, mergeRelationshipTypes, normalizeRelationship, visibleRelationships, layoutSocialCircles, populateTypes };", context);
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
atlas.state.charactersById.set("maribelle", { name: "Maribelle" });
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
  assert.equal(line.attributes.y1, 0);
  assert.ok(line.attributes.y2 > 150 && line.attributes.y2 < 180);
  assert.equal(line.attributes["marker-end"], "url(#parent-child-arrow)");
  assert.equal(elements.get("#map-edge-labels").children[0].attributes["aria-label"], "Godfrey is parent of Maribelle");
}
// The remote update's other directed roles continue to point from student to mentor.
const mentor = { ...parent, typeId: "societal-mentor", displayName: "Mentor", category: "Societal" };
const student = { ...child, typeId: "societal-student", displayName: "Student", category: "Societal" };
atlas.state.graphPairs = atlas.buildPairs([mentor, student]);
atlas.renderGraph();
const roleLine = elements.get("#map-edges").children[0];
assert.equal(roleLine.attributes.y1, 200);
assert.ok(roleLine.attributes.y2 > 27 && roleLine.attributes.y2 < 50);
assert.equal(roleLine.attributes["marker-end"], "url(#relationship-arrow)");
assert.equal(elements.get("#map-edge-labels").children[0].attributes["aria-label"], "Student of");
console.log("Relationship cards, named parent-to-child arrows, and societal arrows passed.");

const catalog = JSON.parse(await readFile(new URL("../docs/relationship-catalog.json", import.meta.url), "utf8"));
atlas.state.relationshipTypes = atlas.mergeRelationshipTypes([
  { id: "societal-student", displayName: "Student", category: "Societal", inverseId: "societal-mentor" },
  { id: "future-type", displayName: "Future type", category: "Social", inverseId: "future-type" }
], catalog);
assert.equal(atlas.state.relationshipTypes.length, catalog.length + 1);
assert.equal(atlas.normalizeRelationship(student).displayName, "Mentee");
assert.equal(atlas.relationshipText(student), "Mentee of");
assert.equal(atlas.relationshipText(parent), "Parent of");
assert.equal(atlas.relationshipText(child), "Child of");
assert.equal(atlas.mergeRelationshipTypes(null, null).length, 0);
assert.equal(atlas.mergeRelationshipTypes([null, {}], {}).length, 0);
assert.equal(atlas.mergeRelationshipTypes(catalog, null).length, catalog.length);

let directed = 0;
for (const type of catalog) {
  const inverse = catalog.find(candidate => candidate.id === type.inverseId);
  const forward = atlas.normalizeRelationship({ ...parent, typeId: type.id, displayName: "Old snapshot label" });
  const backward = atlas.normalizeRelationship({ ...child, typeId: inverse.id, displayName: "Old inverse label" });
  const records = [forward, backward];
  const current = atlas.buildPairs(records)[0];
  assert.equal(atlas.buildPairs(records).length, 1, `${type.id} duplicated the inverse edge`);
  assert.equal(atlas.relationshipText(forward), type.perspectiveLabel);
  const direction = atlas.relationshipDirection(current);
  assert.equal(direction, atlas.relationshipDirection({ ...current, records: [...records].reverse() }));
  if (type.id === inverse.id) assert.equal(direction, null, `${type.id} should be symmetric`);
  else {
    directed++;
    assert.equal(direction.typeId, type.preferDirection ? type.id : inverse.id);
    atlas.state.graphPairs = [current];
    for (const selected of ["godfrey", "maribelle"]) {
      atlas.state.selectedId = selected;
      atlas.renderGraph();
      const arrow = elements.get("#map-edges").children[0];
      assert.ok(arrow.attributes["marker-end"], `${type.id} is missing its arrow`);
      if (!["biological-parent", "biological-child", "adoptive-parent", "adoptive-child"].includes(type.id)) {
        assert.equal(arrow.attributes.y1, direction.sourceCharacterId === "godfrey" ? 0 : 200);
        assert.equal(elements.get("#map-edge-labels").children[0].attributes["aria-label"], atlas.relationshipText(direction));
      }
    }
  }
  atlas.state.relationships = records;
  atlas.state.category = type.category;
  atlas.state.typeId = type.id;
  assert.equal(atlas.visibleRelationships().length, 2, `${type.id} filter dropped its inverse`);
  if (!["Biological", "Adoptive"].includes(type.category))
    assert.equal(atlas.buildTreePairs(records).length, 0, `${type.id} became a family-tree generation`);
}

// Multiple current and historical ties remain visible; inverse records do not duplicate the line.
const sharedTypes = ["social-close-friend", "social-friendly-rival", "romantic-spouse", "romantic-former-spouse", "societal-master"];
const shared = sharedTypes.flatMap(typeId => {
  const type = catalog.find(candidate => candidate.id === typeId);
  return [atlas.normalizeRelationship({ ...parent, typeId }), atlas.normalizeRelationship({ ...child, typeId: type.inverseId })];
});
atlas.state.relationships = shared;
atlas.state.category = "";
atlas.state.typeId = "";
atlas.state.graphPairs = atlas.buildPairs(atlas.visibleRelationships());
assert.equal(atlas.state.graphPairs.length, 1);
assert.equal(atlas.state.graphPairs[0].records.length, 10);
atlas.renderGraph();
assert.match(elements.get("#map-edge-labels").children[0].attributes["aria-label"], /\+3$/);
atlas.state.focusId = "godfrey";
atlas.state.characters = [{ publicId: "godfrey", name: "Godfrey" }, { publicId: "maribelle", name: "Maribelle" }];
atlas.layoutSocialCircles(atlas.state.graphPairs);
assert.equal(atlas.state.positions.size, 2);
assert.equal(atlas.state.socialRings.length, 1);
console.log(`Expanded catalog: ${catalog.length} labels/filters, ${directed} directional perspectives, coexistence, family exclusions, and social circles passed.`);

// Category selection gates the status picker instead of presenting the entire catalog.
atlas.state.category = "";
atlas.state.typeId = "romantic-spouse";
atlas.populateTypes();
const picker = elements.get("#type-filter");
assert.equal(picker.disabled, true);
assert.equal(picker.children.length, 1);
assert.equal(picker.children[0].textContent, "Choose a category first");
assert.equal(atlas.state.typeId, "");
for (const category of new Set(catalog.map(type => type.category))) {
  atlas.state.category = category;
  atlas.populateTypes();
  assert.equal(picker.disabled, false);
  const options = picker.children.slice(1);
  assert.equal(options.length, atlas.state.relationshipTypes.filter(type => type.category === category).length);
  assert.ok(options.every(option => atlas.state.relationshipTypes.find(type => type.id === option.value)?.category === category));
}
atlas.state.category = "";
atlas.populateTypes();
assert.equal(picker.disabled, true);
assert.equal(picker.children.length, 1);
console.log("Category-first picker, category isolation, and returning to All ties passed.");
