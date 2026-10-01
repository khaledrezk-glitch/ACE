// Lessons: what Claude (and the team) learned while working, served back automatically.
//
// A lesson is a short, reusable piece of knowledge:
//   fact        about a model or project ("The offices are rooms 301, 401 and 501 on L3-L5")
//   howto       a technique that worked ("Chair-Breuer faces +Y; place it 100 mm beyond the desk edge")
//   mistake     something that failed and why, with the fix ("ViewPlan crop is locked by a scope box: use a temporary view")
//   preference  what the user wants ("Khaled prefers 1.2 m aisles and 72-inch desks")
// Scope: "model" (one model), "project" (all models of a project) or "ace" (everywhere).
// Stored as JSON lines in %APPDATA%\ACE-RevitMCP\lessons (personal) and, when shared, in the team folder.
import fs from "node:fs";
import path from "node:path";
import { DATA_DIR, ROOT, readConfig, teamScriptsDir } from "./core.js";

// Reviewed lessons that ship with every install (curated from team lessons by the maintainers).
export const BUILTIN_LESSONS = path.join(ROOT, "lessons");
const BUILTIN_FILE = "built-in.jsonl";
export const USER_LESSONS = process.env.ACE_REVIT_LESSONS || path.join(DATA_DIR, "lessons");

export function teamLessonsDir() {
  if (process.env.ACE_TEAM_LESSONS) return process.env.ACE_TEAM_LESSONS;
  const cfg = readConfig();
  if (cfg?.teamLessonsDir) return cfg.teamLessonsDir;
  const scripts = teamScriptsDir();
  return scripts ? path.join(path.dirname(scripts), "lessons") : null;
}

const FILE = "lessons.jsonl";
const KINDS = ["fact", "howto", "mistake", "preference"];
const SCOPES = ["model", "project", "ace"];

function readDir(dir, source, file = FILE) {
  const out = [];
  try {
    for (const line of fs.readFileSync(path.join(dir, file), "utf8").split(/\r?\n/)) {
      if (!line.trim()) continue;
      try { out.push({ ...JSON.parse(line), source }); } catch { /* skip a bad line */ }
    }
  } catch { /* no file yet */ }
  return out;
}

function append(dir, entry) {
  fs.mkdirSync(dir, { recursive: true });
  fs.appendFileSync(path.join(dir, FILE), JSON.stringify(entry) + "\n");
}

const norm = (s) => String(s || "").trim().toLowerCase();

/** All current lessons (retired ones removed), personal and team, newest first. */
export function allLessons() {
  const team = teamLessonsDir();
  const rows = [...readDir(USER_LESSONS, "personal"), ...(team ? readDir(team, "team") : []), ...readDir(BUILTIN_LESSONS, "built-in", BUILTIN_FILE)];
  const retired = new Set(rows.filter((r) => r.retired).map((r) => r.id));
  const seen = new Set();
  return rows
    .filter((r) => !r.retired && r.id && !retired.has(r.id) && !seen.has(r.id) && seen.add(r.id))
    .sort((a, b) => String(b.time).localeCompare(String(a.time)));
}

export function recordLesson({ lesson, kind = "fact", scope = "model", model, project, tags, share_with_team }) {
  if (!lesson || lesson.trim().length < 8) throw new Error("Write the lesson as one or two clear sentences.");
  if (!KINDS.includes(kind)) throw new Error(`kind must be one of ${KINDS.join(", ")}`);
  if (!SCOPES.includes(scope)) throw new Error(`scope must be one of ${SCOPES.join(", ")}`);
  if (scope === "model" && !model) throw new Error("A model lesson needs 'model' (the model title from get_model_brief).");
  if (scope === "project" && !project) throw new Error("A project lesson needs 'project' (the project name or number).");
  const duplicate = allLessons().find((l) => norm(l.lesson) === norm(lesson) && l.scope === scope && norm(l.model) === norm(model) && norm(l.project) === norm(project));
  if (duplicate) return { ...duplicate, note: "Already known; nothing added." };
  const entry = {
    id: `L${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`,
    time: new Date().toISOString(),
    kind, scope, lesson: lesson.trim(),
    model: scope === "model" ? model : undefined,
    project: scope === "project" ? project : undefined,
    tags: Array.isArray(tags) ? tags : undefined,
    user: process.env.USERNAME || process.env.USER || undefined,
  };
  append(USER_LESSONS, entry);
  const team = share_with_team ? teamLessonsDir() : null;
  if (team) append(team, entry);
  return { ...entry, sharedWithTeam: !!team, note: share_with_team && !team ? "No team folder is configured (team.json), so it was saved for you only." : undefined };
}

export function retireLesson(id, reason) {
  const l = allLessons().find((x) => x.id === id);
  if (!l) throw new Error(`No current lesson with id ${id}.`);
  // Built-in lessons can't be edited here: retiring one hides it for this user and flags it for the maintainers.
  const mark = { id, retired: true, time: new Date().toISOString(), reason };
  append(USER_LESSONS, mark);
  const team = teamLessonsDir();
  if (l.source === "team" && team) append(team, mark);
  return { retired: id, lesson: l.lesson };
}

/** Lessons that apply to this model / project, plus the general ones. */
export function lessonsFor({ model, project, query, limit = 40 } = {}) {
  const m = norm(model), p = norm(project), q = norm(query);
  return allLessons()
    .filter((l) => l.scope === "ace"
      || (l.scope === "model" && m && (norm(l.model) === m || m.includes(norm(l.model)) || norm(l.model).includes(m)))
      || (l.scope === "project" && p && (p.includes(norm(l.project)) || norm(l.project).includes(p))))
    .filter((l) => !q || norm(l.lesson).includes(q) || (l.tags || []).some((t) => norm(t).includes(q)))
    .slice(0, limit)
    .map(({ id, kind, scope, lesson, model: lm, project: lp, source, time }) => ({ id, kind, scope, lesson, model: lm, project: lp, source, date: String(time).slice(0, 10) }));
}

export function lessonStats() {
  const all = allLessons();
  const by = (k) => Object.fromEntries([...new Set(all.map((l) => l[k]))].map((v) => [v, all.filter((l) => l[k] === v).length]));
  return { total: all.length, byKind: by("kind"), byScope: by("scope"), bySource: by("source") };
}
