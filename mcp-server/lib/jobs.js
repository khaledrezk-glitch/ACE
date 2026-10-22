// Background jobs: a long tool call answered at once with a job id, its reply written to a job file when it ends.
// The file is the source of truth, so a separate ACE server process (or a person) can read the outcome.
import fs from "node:fs";
import path from "node:path";
import { randomUUID } from "node:crypto";
import { DATA_DIR, backgroundRuns, localDate } from "./core.js";
import { journal } from "./safety.js";

export const JOBS_DIR = () => process.env.ACE_REVIT_JOBS || path.join(DATA_DIR, "jobs");
const fileOf = (id) => path.join(JOBS_DIR(), `${id}.json`);
const write = (job) => { fs.mkdirSync(JOBS_DIR(), { recursive: true }); const tmp = `${job.file}.tmp`; fs.writeFileSync(tmp, JSON.stringify(job, null, 1)); fs.renameSync(tmp, job.file); };

// Revit runs one request at a time: this server's jobs run one after another, each starting when the one before ended.
let tail = Promise.resolve();

/** A new job file for this call (running, or queued behind this server's earlier jobs); the args are kept for the record. */
export function start(tool, args) {
  const id = `${tool}-${localDate(new Date(), { time: true }).replace(/[^0-9]/g, "").slice(0, 14)}-${randomUUID().slice(0, 8)}`;
  const behind = [...backgroundRuns.keys()].at(-1) ?? null;
  const job = { id, tool, state: behind ? "queued" : "running", behind, started: new Date().toISOString(), pid: process.pid, args: { ...args, background: undefined }, file: fileOf(id) };
  write(job);
  backgroundRuns.set(id, job);
  return job;
}
/** Runs the job when the jobs before it have ended; its promise settles with run's. */
export function enqueue(job, run) {
  const p = tail.then(() => {
    if (job.state === "queued") { job.state = "running"; job.started = new Date().toISOString(); write(job); }
    return run();
  }).finally(() => backgroundRuns.delete(job.id));
  tail = p.catch(() => {});
  return p;
}
/** The call ended: its reply (text parts) and whether it was an error, in the job file AND the journal (never lost). */
export function finish(job, result) {
  const textOut = (result?.content ?? []).filter((c) => c?.type === "text").map((c) => c.text).join("\n");
  const state = result?.isError ? "failed" : "done";
  write({ ...job, state, ended: new Date().toISOString(), reply: textOut });
  journal({ title: `Background job ${job.id} (${job.tool}) ${state}`, outcome: `${state}; full reply in ${job.file} (job_status)`, reply: textOut });
}
const read = (id) => { try { return JSON.parse(fs.readFileSync(fileOf(id), "utf8")); } catch { return null; } };
const alive = (pid) => { try { process.kill(pid, 0); return true; } catch (e) { return e?.code === "EPERM"; } };

/** job_status text: one job in full, or the latest ten in one line each. A running job whose server is gone is said so. */
export function statusText(id) {
  if (id) {
    if (!/^[\w.-]+$/.test(id)) return `"${id}" is not a job id.`;
    const j = read(id);
    if (!j) return `No job ${id} in ${JOBS_DIR()}.`;
    if (j.state === "queued" && alive(j.pid))
      return `Job ${id} (${j.tool}) is queued behind job ${j.behind}: Revit runs one request at a time, it starts when that one ends. Ask again later; do not start it again.`;
    if (j.state === "running" || j.state === "queued") {
      const secs = Math.round((Date.now() - Date.parse(j.started)) / 1000);
      return alive(j.pid) ? `Job ${id} (${j.tool}) is still running (${secs} s). Ask again later; do not start it again.`
        : j.state === "queued" ? `Job ${id} (${j.tool}) never started (nothing ran): its ACE server (pid ${j.pid}) has ended. Start it again if it is still needed.`
        : `Job ${id} (${j.tool}) never finished: its ACE server (pid ${j.pid}) has ended. Revit may have completed the work: check its run record or the output folder before running it again.`;
    }
    return `Job ${id} (${j.tool}) ${j.state} at ${j.ended} (${Math.round((Date.parse(j.ended) - Date.parse(j.started)) / 1000)} s).\n\n${j.reply}`;
  }
  let files = [];
  try { files = fs.readdirSync(JOBS_DIR()).filter((f) => f.endsWith(".json")); } catch {}
  const jobs = files.map((f) => read(f.replace(/\.json$/, ""))).filter(Boolean).sort((a, b) => String(b.started).localeCompare(String(a.started))).slice(0, 10);
  return jobs.length ? jobs.map((j) => `${j.id}: ${j.state}, started ${j.started}`).join("\n") : "No background jobs yet.";
}
