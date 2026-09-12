import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

// Exercise rendering and clipboard behavior without a browser or live server data.
class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.attributes = {}; this.events = {}; this.textContent = ""; }
  append(...children) { this.children.push(...children); }
  setAttribute(key, value) { this.attributes[key] = value; }
  addEventListener(name, fn) { this.events[name] = fn; }
}
let copied = null;
const context = { window: {}, document: { createElement: tag => new Element(tag) },
  navigator: { clipboard: { writeText: async value => { copied = value; } } } };
runInNewContext(await readFile(new URL("../docs/owner.js", import.meta.url), "utf8"), context);
const create = context.window.CharacterAttribution.create;
const publicOwner = create({ displayName: "Writer <script>not code</script>", discordId: "111111111111111111" });
assert.equal(publicOwner.children[0].textContent, "By Writer <script>not code</script>");
assert.equal(publicOwner.children.filter(child => child.tag === "button").length, 1);
assert.equal(publicOwner.children[1].attributes["aria-label"], "Copy Discord ID for Writer <script>not code</script>");
await publicOwner.children[1].events.click({ stopPropagation() {} });
assert.equal(copied, "111111111111111111");
assert.equal(publicOwner.children[2].textContent, "Copied!");
const hiddenOwner = create({ displayName: "Writer" });
assert.equal(hiddenOwner.children[0].textContent, "By Writer");
assert.equal(hiddenOwner.children.filter(child => child.tag === "button").length, 0);
assert.equal(hiddenOwner.children[1].textContent, "Discord ID not published");
assert.equal(create({ displayName: "Writer", discordId: 111111111111111111 }).children.filter(child => child.tag === "button").length, 0);
assert.equal(create(null).children[0].textContent, "Owner not published");
context.navigator.clipboard.writeText = async () => { throw new Error("Clipboard permission denied"); };
await publicOwner.children[1].events.click({ stopPropagation() {} });
assert.equal(publicOwner.children[2].textContent, "Copy manually: 111111111111111111");
console.log("Passed 11 owner attribution/clipboard checks.");
