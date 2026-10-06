# The ACE development team (agents) and the development radar

Two things keep ACE improving without wasting the owner's time or tokens:

1. **The development radar** (in the product): a local monitor of how ACE is used, turned into development candidates.
2. **The development team** (for maintainers): ten agents in `.claude/agents/`, run as one saved workflow
   (`.claude/workflows/ace-dev-cycle.js`) in two short runs with one owner decision in between.

Nothing runs on its own. Both cost nothing until someone asks.

## 1. The development radar

Every tool call is already written to the local call log (`%APPDATA%\ACE-RevitMCP\logs\mcp-calls.jsonl`, never sent
anywhere). The radar (`mcp-server/lib/radar.js`) reads it only when a learning report is made (ask Claude for a
learning report, or `report_issue` kind `learning`). It groups calls into **tasks** (a pause of 15 minutes starts a new
one) and flags:

| Signal | Means | Typical fix |
|---|---|---|
| Tasks with 15+ calls | A job takes too many interactions | A built-in script, a native command, or one script with a plan |
| 3+ previews for one apply | Claude guessed, failed and retried | Better guide text, a helper, or one question up front |
| 3+ failed calls in a task | Something is unreliable | Fix the guide, the script or the command |
| Tasks above ~60k tokens, tools with large replies | Expensive | Smaller defaults, summaries, paging |
| The same code 3+ times | A recipe people need | A tested built-in script |

The report's **Development radar** section lists these with the numbers. Shared learning reports (`share_with_team`)
are what the usage scout reads.

## 2. The team

| Group | Agent | Does | Model |
|---|---|---|---|
| Ideas | `ace-ideas-owner` | Candidates from the owner's ideas, the concept and `docs/BACKLOG.md` | Sonnet, read-only |
| Ideas | `ace-ideas-usage` | Candidates from real usage: the radar, learning and issue reports, lessons, failing bench cases | Sonnet, read-only |
| Ideas | `ace-ideas-market` | Candidates from comparable tools (Navisworks, ACC, Model Checker, pyRevit, other Revit MCP servers) | Sonnet, read-only + 5 web searches |
| Build | `ace-planner` | Turns approved items into a plan split by file ownership, with checks and a bench case | Session model, read-only |
| Build | `ace-builder-addin` | Implements the add-in part, runs the add-in checks | Session model |
| Build | `ace-builder-server` | Implements the server, scripts and docs part, runs the server checks | Session model |
| Review | `ace-review-bugs` | Real bugs with a failure scenario | Sonnet, report only |
| Review | `ace-review-efficiency` | Wasted work, tokens, extra interactions, duplication | Sonnet, report only |
| Review | `ace-review-safety` | The CLAUDE.md invariants, workshared models, brand rules; runs the touched checks | Sonnet, report only |
| Monitor | `ace-monitor` | Triage into a short list; the final report (done, cost, what is left, next action) | Sonnet, read-only |

## 3. One cycle, one decision

1. **Propose** (4 agents, read-only):
   `Workflow({ name: "ace-dev-cycle", args: { mode: "propose", max: 3 } })`, optionally with `focus` (e.g. "fewer
   steps") or `reports` (a folder of shared learning reports). Returns a short list of at most `max` items, what was
   dropped and the backlog updates.
2. **The owner approves** the short list (or edits it), in one message.
3. **Build** (up to 9 agents): `Workflow({ name: "ace-dev-cycle", args: { mode: "build", items: [ ... ] } })`. The
   planner splits the work; the two builders work at the same time on disjoint files; three reviewers check the result;
   one fix round handles high-severity findings only; the monitor writes the report. Nothing is committed by the agents.
4. **The maintainer session** reads the report, runs the checks once more, commits, bumps the version, builds the
   package. The owner installs it and tests in Revit.

## 4. Rules that keep it cheap

- **No standing processes.** No loops, no schedules, no polling. A cycle runs only when started.
- **Hard limits:** at most 5 items per cycle (3 by default), one fix round, one triage, one report.
- **The right model for the job:** scouts, reviewers and the monitor use Sonnet; only the planner and the builders use
  the session model.
- **Short inputs:** scouts read the backlog, the concept and the radar, not the whole code base; reviewers read the
  diff, not the repository.
- **One decision per cycle** for the owner: approve the short list. Everything else is in the report.
- **Proof before release:** offline checks in the cycle; the benchmark and the live test in Revit by the owner.

Rough cost: a propose run is about the size of one long conversation; a build run about three to six, depending on the
items.
