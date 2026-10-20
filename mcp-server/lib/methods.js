// Office methods (Claude skills) linked to ACE tools: guides/methods/index.json is the registry, <name>.md the text.
// Tool descriptions, revit_guide topics and capture_method's case all read the registry, so nothing drifts.
import fs from "node:fs";
import path from "node:path";
import { GUIDES_DIR } from "./core.js";

export const METHODS_DIR = path.join(GUIDES_DIR, "methods");

/** The registry entries ([] when there is none); an entry without name or file, or whose file is missing, is an error. */
export function registry(dir = METHODS_DIR) {
  const f = path.join(dir, "index.json");
  if (!fs.existsSync(f)) return [];
  const list = JSON.parse(fs.readFileSync(f, "utf8").replace(/^﻿/, "")).methods ?? [];
  for (const m of list) {
    if (!m?.name || !m.file || !Array.isArray(m.tools)) throw new Error(`${f}: every method needs name, file and tools.`);
    if (!fs.existsSync(path.join(dir, m.file))) throw new Error(`${f}: method ${m.name} names ${m.file}, which is not in ${dir}.`);
  }
  return list;
}
export const forTool = (tool, list = registry()) => list.filter((m) => m.tools.includes(tool));
/** The line a linked tool's description ends with, or "". */
export const descriptionLine = (tool, list = registry()) => forTool(tool, list).map((m) => `Office method: revit_guide methods/${m.name}`).join(". ");
export const topics = (list = registry()) => list.map((m) => `methods/${m.name}`);
export const listText = (list = registry()) => list.length
  ? `Office methods (read one with revit_guide methods/<name>):\n${list.map((m) => `- methods/${m.name} (v${m.version ?? 1}, ${m.updated ?? "?"}): tools ${m.tools.join(", ")}. ${m.sourceCase ?? ""}`.trim()).join("\n")}`
  : "No office methods yet.";
export const read = (name, list = registry()) => {
  const m = list.find((x) => x.name === name);
  return m ? fs.readFileSync(path.join(METHODS_DIR, m.file), "utf8") : null;
};
