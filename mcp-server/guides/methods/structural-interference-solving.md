# Office method: structural interference solving (version 1)

Office method (Khaled, 20 Oct 2026, from his solve of one structural model of a live project, 72 overlaps down to 1). Concrete only; steel and stairs left out. Self-check one model only (with_model = itself). Join priority Foundation > Column > Wall > Slab > Beam. Join all touching concrete, not only clashing pairs. Trim a beam or wall end to the face it runs into, only when a join can't clear the overlap: shorten only, max 1200 mm, each trim shown in the preview. Floor boundary edits are manual and reported. Report annotation loss with a count. A case no rule covers: ask Khaled to solve one example, record it with capture_method, and propose the rule; it goes into office rules only after he confirms.

## With ACE's tools

- run_clash_test with with_model = this model (concrete; exclude_materials steel), then clash_resolve action plan.
- The office or project rules/clash-resolve.json carries the order, scope "all-touching" and trim_to_face
  (allowed, preview_required, max_mm: 1200); project_rules section "clash-resolve" shows what is in force.
- clash_resolve action fix: dry_run, read every trim and the per-pair summary to the user, then the identical call.
- coordination_report for what is left (report only, floor boundary edits).
- capture_method for a case no rule covers; proposed rules need Khaled's confirmation.
