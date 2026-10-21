// purge_unused: a full Purge Unused (Revit's own unused list, Document.GetUnusedElements) in passes until nothing is left,
// the owner's method (three passes reach zero). Not an element delete: only what Revit calls unused goes, one Delete of
// the whole set per pass. Specific types are purged by hand in Revit's dialog. Pure functions for index.js (planExecute).
import { blockLine } from "./paramexec.js";

const HOW = {
  borrowed: "ask the holder to synchronise and relinquish, then run the dry run again",
  central: "reload latest, then run the dry run again",
  "read-only": "open an editable copy of the model (not read-only, not a link or family)",
};
export const blocksOf = (r) => (r?.blocked ?? []).map((b) => ({ ...b, how: b.how ?? HOW[b.code] }));
export const planOf = (r) => r?.plan ?? [];
// Every pass the result knows: pass 1 from check_only, all passes from a dry run (its planHash covers every pass's set).
export const planIdentity = (r) => ({ hash: r?.planHash ?? null, steps: planOf(r).map((p) => [p.id, p.kind, p.count ?? null]) });
export const rolledBackOf = (r) => r?.rolledBack === true || r?.verify?.ok === false;

const cats = (list = []) => list.slice(0, 20).map((c) => `${c.category} ${c.deleted != null && c.deleted !== c.found ? `${c.deleted}/${c.found}` : c.found}`).join(", ");
const STOP = { "nothing-left": "a pass found nothing", "max-passes": "max_passes reached", "no-progress": "a pass deleted nothing" };

/** The reply: per pass found / deleted / by category / names, the total, what is left after the last pass, the planHash. */
export function planText(r, { model, mode, rolledBack = false, a = {} }) {
  const head = mode === "check" ? "check, read-only, nothing changed" : mode === "dry run" ? "dry run, rolled back" : rolledBack ? "NOT applied: rolled back" : "applied as ONE undo step";
  const lines = [`PURGE UNUSED in ${model} (${head}): Revit's Purge Unused (${r?.source ?? "Document.GetUnusedElements"}) in passes until nothing is left; only what Revit calls unused is deleted, one delete of the whole set per pass.`];
  const scope = ["full purge, all categories", `max ${r?.maxPasses ?? a.max_passes ?? 3} passes`];
  if (r?.kept) scope.push(`${r.kept} Revit link type(s) never purged`);
  lines.push(`Scope: ${scope.join("; ")}.`);
  const blocks = blocksOf(r);
  if (blocks.length) lines.push(`Blocked (nothing changed):\n${blocks.map((b) => `- ${blockLine(b)}`).join("\n")}`);
  if (mode === "check") {
    const p = planOf(r)[0];
    lines.push(p ? `Pass 1 would purge ${p.count}: ${cats(p.byCategory)}${p.names?.length ? ` (e.g. ${p.names.slice(0, 5).join("; ")})` : ""}. Later passes (what becomes unused once these are gone) show in the dry run.` : "Nothing is unused: nothing to purge.");
  } else {
    const verb = mode === "dry run" || rolledBack ? "would delete" : "deleted";
    for (const p of r?.passes ?? [])
      lines.push(`Pass ${p.pass}: found ${p.found}, ${verb} ${p.deleted}${p.alsoDeleted ? ` (+${p.alsoDeleted} dependent)` : ""}${p.failed ? `, skipped ${p.failed}` : ""}${p.attempts > 1 ? ` (${p.attempts} deletes: the set was split)` : ""}. ${cats(p.byCategory)}${p.names?.length ? `; e.g. ${p.names.join("; ")}` : ""}.`);
    const total = r?.totalDeleted ?? 0;
    lines.push(`Total: ${verb} ${total} in ${(r?.passes ?? []).length} pass(es)${r?.stoppedBecause ? ` (stopped: ${STOP[r.stoppedBecause] ?? r.stoppedBecause})` : ""}.`);
    const d = r?.unusedLeftDetail;
    if (r?.unusedLeft != null) lines.push(`Unused left after the last pass: ${r.unusedLeft}${r.unusedLeft && d ? ` (link types ${d.linkTypes ?? 0}, could not be purged ${d.skipped}, other ${d.other})` : ""}.`);
    const skipped = (r?.passes ?? []).flatMap((p) => p.skipped ?? []);
    if (skipped.length) lines.push(`Could not be purged (left in the model):\n${skipped.slice(0, 20).map((s) => `- ${s.id}${s.name ? ` ${s.name}` : ""}${s.category ? ` (${s.category})` : ""}: ${s.error}`).join("\n")}`);
    const vf = r?.verify;
    if (vf) lines.push(vf.ok ? `Self-check: ok (${vf.checked ?? "?"} element(s) read back: link types present, deleted ones gone).`
      : `Self-check FAILED: ${(vf.mismatches ?? []).slice(0, 5).map((m) => `#${m.id}: ${m.what}`).join("; ")}; rolled back, nothing changed.`);
    if (mode === "dry run" && (r?.plan ?? []).length) lines.push(`Plan: ${r.plan.map((p) => `${p.id} ${p.count}`).join(", ")} (the apply must find exactly these, pass by pass).`);
  }
  if (r?.planHash) lines.push(`planHash: ${r.planHash}`);
  return lines.join("\n");
}
