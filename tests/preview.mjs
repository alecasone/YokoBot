// Local-only UI fixture: never reads private bot data or changes the public sample.
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = fileURLToPath(new URL("../docs/", import.meta.url));
const id = n => `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
const names = ["Aster Vale", "Birch Vale", "Cedar Ash", "Dahlia Ash", "Elowen Reed", "Finn Reed", "Garnet Rook", "Hazel Rook"];
const types = [
  ["biological-parent", "Biological parent", "Biological", "biological-child"],
  ["biological-child", "Biological child", "Biological", "biological-parent"],
  ["social-friend", "Friend", "Social", "social-friend"],
  ["social-rival", "Rival", "Social", "social-rival"],
  ["romantic-spouse", "Spouse", "Romantic", "romantic-spouse"],
  ["romantic-dating", "Dating", "Romantic", "romantic-dating"],
  ["societal-mentor", "Mentor", "Societal", "societal-student"],
  ["societal-student", "Student", "Societal", "societal-mentor"],
  ["adoptive-parent", "Adoptive parent", "Adoptive", "adoptive-child"],
  ["adoptive-child", "Adoptive child", "Adoptive", "adoptive-parent"]
].map(([id, displayName, category, inverseId]) => ({ id, displayName, category, inverseId }));
const edge = (a, b, typeId) => {
  const type = types.find(type => type.id === typeId);
  return { sourceCharacterId: id(a), targetCharacterId: id(b), typeId, displayName: type.displayName,
    category: type.category, isInferred: false, explanation: null };
};
const direct = [[1, 2, "biological-parent"], [2, 4, "biological-parent"], [3, 4, "adoptive-parent"],
  [1, 2, "social-friend"], [1, 3, "social-rival"], [2, 4, "social-friend"], [4, 5, "social-friend"],
  [6, 7, "social-rival"], [1, 3, "romantic-spouse"], [4, 5, "romantic-dating"],
  [1, 6, "societal-mentor"], [6, 8, "societal-mentor"]];
const fixture = { schemaVersion: 4, generatedAt: "2026-09-11T12:00:00Z",
  branding: { name: "Helios", logoLetter: "H", archiveTitle: "Character Archive",
    atlasTitle: "Relationship Atlas", tagline: "Local preview · fabricated relationships only." },
  characters: names.map((name, i) => ({ publicId: id(i + 1), name, aliases: [], age: String(70 - i * 7),
    owner: i % 2 ? { displayName: "Example writer · ID hidden" } : { displayName: "Example writer", discordId: "111111111111111111" },
    region: i < 4 ? "North" : "South", occupation: "Storyteller", properties: {}, approvedAt: "2026-09-01T12:00:00Z" })),
  relationshipTypes: types,
  relationships: direct.flatMap(([a, b, typeId]) => [edge(a, b, typeId), edge(b, a, types.find(type => type.id === typeId).inverseId)]) };
const mime = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css", ".json": "application/json", ".png": "image/png", ".svg": "image/svg+xml" };
createServer(async (request, response) => {
  try {
    const url = new URL(request.url, "http://127.0.0.1:4188");
    const pathname = decodeURIComponent(url.pathname);
    if (pathname === "/data/characters.json") {
      response.writeHead(200, { "Content-Type": "application/json", "Cache-Control": "no-store" });
      response.end(JSON.stringify(fixture));
      return;
    }
    const target = path.resolve(root, "." + (pathname.endsWith("/") ? pathname + "index.html" : pathname));
    if (!target.startsWith(root)) { response.writeHead(403); response.end(); return; }
    const content = await readFile(target);
    response.writeHead(200, { "Content-Type": mime[path.extname(target)] || "application/octet-stream", "Cache-Control": "no-store" });
    response.end(content);
  } catch { response.writeHead(404); response.end("Not found"); }
}).listen(4188, "127.0.0.1", () => console.log("Fixture preview: http://127.0.0.1:4188/relationships.html (Ctrl+C to stop)"));
