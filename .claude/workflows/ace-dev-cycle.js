export const meta = {
  name: 'ace-dev-cycle',
  description: 'ACE development cycle: propose (idea scouts + monitor triage) or build (live-check gate, planner, 2 builders, 3 reviewers, one fix round, recheck, monitor report)',
  whenToUse: 'Run with {mode: "propose"} to get a short list for the owner, then {mode: "build", items: [...]} with the approved items. Two runs, one owner decision in between.',
  phases: [
    { title: 'Ideas', detail: 'owner and concept scout; real usage when reports are given; benchmark when asked' },
    { title: 'Triage', detail: 'monitor merges and ranks into a short list' },
    { title: 'Gate', detail: 'no new building while too many items wait for a live check in Revit' },
    { title: 'Plan', detail: 'planner splits the work by file ownership' },
    { title: 'Build', detail: 'add-in and server builders in parallel' },
    { title: 'Review', detail: 'bugs, efficiency, safety' },
    { title: 'Fix', detail: 'one round for high-severity findings only' },
    { title: 'Recheck', detail: 'offline checks again after the fixes' },
    { title: 'Report', detail: 'monitor: done, cost, what is left' },
  ],
}

// One owner decision per cycle: "propose" returns a short list (4 agents, read-only); "build" implements the approved
// items (up to 10 agents). Hard limits keep it cheap: at most args.max items (default 3), one fix round, no loops.
// Building waits while more than args.maxLiveCheck (default 3) backlog items are "live check": the bottleneck is
// verification in Revit, not new features. args.force: true overrides the gate.
const mode = (args && args.mode) || 'propose'
const max = Math.min((args && args.max) || 3, 5)

const CANDIDATES = {
  type: 'object',
  properties: {
    candidates: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          title: { type: 'string' },
          why: { type: 'string', description: 'what the user gains' },
          evidence: { type: 'string' },
          stepsSaved: { type: 'string', description: 'interactions or tokens saved, if any' },
          effort: { type: 'string', enum: ['S', 'M', 'L'] },
          area: { type: 'string', enum: ['addin', 'server', 'both', 'docs'] },
          backlogId: { type: 'string', description: 'id in docs/BACKLOG.md, or "new"' },
        },
        required: ['title', 'why', 'effort', 'area'],
      },
    },
    note: { type: 'string' },
  },
  required: ['candidates'],
}

const SHORTLIST = {
  type: 'object',
  properties: {
    shortlist: { type: 'array', items: { type: 'object', properties: { title: { type: 'string' }, why: { type: 'string' }, effort: { type: 'string' }, area: { type: 'string' }, backlogId: { type: 'string' } }, required: ['title', 'why', 'effort', 'area'] } },
    dropped: { type: 'array', items: { type: 'string' } },
    backlogUpdates: { type: 'array', items: { type: 'string' }, description: 'new rows or status changes for docs/BACKLOG.md' },
    note: { type: 'string' },
  },
  required: ['shortlist'],
}

const GATE = {
  type: 'object',
  properties: { items: { type: 'array', items: { type: 'object', properties: { id: { type: 'string' }, title: { type: 'string' }, test: { type: 'string' } }, required: ['id', 'test'] } } },
  required: ['items'],
}

const CHECKS = {
  type: 'object',
  properties: { passed: { type: 'boolean' }, results: { type: 'string', description: 'each check and its result, one line each' } },
  required: ['passed', 'results'],
}

const PLAN = {
  type: 'object',
  properties: {
    addin: { type: 'string', description: 'the add-in builder\'s instructions (files, steps, checks); empty if none' },
    server: { type: 'string', description: 'the server builder\'s instructions (files, steps, checks, bench case); empty if none' },
    integration: { type: 'string' },
  },
  required: ['addin', 'server'],
}

const FINDINGS = {
  type: 'object',
  properties: {
    findings: { type: 'array', items: { type: 'object', properties: { severity: { type: 'string', enum: ['high', 'medium', 'low'] }, file: { type: 'string' }, problem: { type: 'string' }, fix: { type: 'string' }, owner: { type: 'string', enum: ['addin', 'server'] } }, required: ['severity', 'problem', 'owner'] } },
    checks: { type: 'string', description: 'offline checks run and their results' },
  },
  required: ['findings'],
}

if (mode === 'propose') {
  phase('Ideas')
  const focus = (args && args.focus) ? `Focus: ${args.focus}. ` : ''
  // The usage scout only runs with shared reports (the call log stays on the PCs); the market scout only when asked.
  const scouts = [['ace-ideas-owner', 'From the owner\'s ideas, the concept and docs/BACKLOG.md']]
  if (args && args.reports) scouts.push(['ace-ideas-usage', `From real usage (development radar, learning and issue reports, lessons, failing bench cases); reports: ${args.reports}`])
  if (args && args.market) scouts.push(['ace-ideas-market', 'From comparable tools'])
  const found = (await parallel(scouts.map(([type, what]) => () =>
    agent(`${focus}${what}: propose ACE development candidates. Give extra weight to fewer steps / fewer interactions and fewer tokens.`,
      { agentType: type, label: type, phase: 'Ideas', schema: CANDIDATES })))).filter(Boolean)
  const all = found.flatMap((r) => r.candidates)
  log(`${all.length} candidates from ${found.length} scouts`)

  phase('Triage')
  const triage = await agent(
    `Triage these ACE development candidates into a short list of at most ${max}, ranked by user value per effort and steps or tokens saved. ` +
    `Say what you dropped and why in a few words, and the backlog updates to make.\n\n${JSON.stringify(all)}`,
    { agentType: 'ace-monitor', label: 'ace-monitor triage', phase: 'Triage', schema: SHORTLIST })
  return { mode, shortlist: triage && triage.shortlist, dropped: triage && triage.dropped, backlogUpdates: triage && triage.backlogUpdates, note: triage && triage.note, spentTokens: budget.spent() }
}

// ---- build: the owner approved args.items -------------------------------------------------------------------------
const items = (args && args.items) || []
if (!items.length) return { error: 'Give items: the approved short list (titles or backlog ids).' }

phase('Gate')
const maxLive = (args && args.maxLiveCheck != null) ? args.maxLiveCheck : 3
const gate = await agent(
  'Read docs/BACKLOG.md and list every item whose status is "live check". For each, write the owner\'s live test in Revit: ' +
  'what to open, what to run or ask Claude, and what a pass looks like (use FIRST-TEST.md and tools/bench/cases.json where they help). Short.',
  { agentType: 'ace-monitor', label: 'ace-monitor gate', phase: 'Gate', schema: GATE })
const waiting = (gate && gate.items) || []
log(`${waiting.length} items waiting for a live check (limit ${maxLive})`)
if (waiting.length > maxLive && !(args && args.force)) {
  return { mode, blocked: `${waiting.length} items wait for a live check in Revit (limit ${maxLive}). Run these tests first, or pass force: true.`, liveTests: waiting, spentTokens: budget.spent() }
}

phase('Plan')
const plan = await agent(`Plan these approved ACE items (at most ${max}): ${JSON.stringify(items.slice(0, max))}. Split the work so the add-in and server builders touch disjoint files.`,
  { agentType: 'ace-planner', label: 'ace-planner', phase: 'Plan', schema: PLAN })
if (!plan) return { error: 'The planner did not return a plan.' }

phase('Build')
// Both builders get the contract between add-in and server (command names, payloads), so the two halves match.
const contract = plan.integration && plan.integration.trim() ? `\n\nIntegration contract (both sides must match it exactly):\n${plan.integration}` : ''
const built = await parallel([
  plan.addin && plan.addin.trim() ? () => agent(`Implement the add-in part of this plan:\n${plan.addin}${contract}`, { agentType: 'ace-builder-addin', label: 'builder addin', phase: 'Build' }) : null,
  plan.server && plan.server.trim() ? () => agent(`Implement the server part of this plan:\n${plan.server}${contract}`, { agentType: 'ace-builder-server', label: 'builder server', phase: 'Build' }) : null,
].filter(Boolean))

phase('Review')
const reviews = (await parallel(['ace-review-bugs', 'ace-review-efficiency', 'ace-review-safety'].map((type) => () =>
  agent(`Review the uncommitted changes for these items: ${JSON.stringify(items.slice(0, max))}.`, { agentType: type, label: type, phase: 'Review', schema: FINDINGS })))).filter(Boolean)
const high = reviews.flatMap((r) => r.findings).filter((f) => f.severity === 'high')
log(`${high.length} high-severity findings`)

phase('Fix')
let fixes = []
if (high.length) {
  // One round only: the monitor's report lists anything still open instead of looping.
  const byOwner = (o) => high.filter((f) => f.owner === o)
  fixes = await parallel([
    byOwner('addin').length ? () => agent(`Fix these review findings in the add-in, then rerun its checks:\n${JSON.stringify(byOwner('addin'))}`, { agentType: 'ace-builder-addin', label: 'fix addin', phase: 'Fix' }) : null,
    byOwner('server').length ? () => agent(`Fix these review findings in the server, then rerun its checks:\n${JSON.stringify(byOwner('server'))}`, { agentType: 'ace-builder-server', label: 'fix server', phase: 'Fix' }) : null,
  ].filter(Boolean))
}

phase('Recheck')
// Fixes are proven before the report: the offline checks run once more on the final state.
const recheck = fixes.length ? await agent(
  'Run the offline checks in CLAUDE.md ("Checks before pushing") that apply to the uncommitted changes. Do not change any file. Report each check and its result.',
  { agentType: 'ace-review-safety', label: 'recheck', phase: 'Recheck', schema: CHECKS }) : null

phase('Report')
const report = await agent(
  `Write the cycle report for the owner. Items: ${JSON.stringify(items.slice(0, max))}\nPlan: ${JSON.stringify(plan)}\nBuilders: ${JSON.stringify(built)}\n` +
  `Reviews: ${JSON.stringify(reviews)}\nFix round: ${JSON.stringify(fixes)}\nRecheck after fixes: ${JSON.stringify(recheck)}\nOutput tokens spent so far: ${budget.spent()}.`,
  { agentType: 'ace-monitor', label: 'ace-monitor report', phase: 'Report' })
return { mode, report, recheck, openFindings: reviews.flatMap((r) => r.findings).filter((f) => f.severity !== 'high'), spentTokens: budget.spent() }
