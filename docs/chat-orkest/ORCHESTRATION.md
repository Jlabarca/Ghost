# How to run CHAT-ORKEST + HARNESS-PARITY

> One orchestrator session (Opus) at `D:\ware`. Cheaper worker sessions (Sonnet) each in one repo path, one phase at a time. The orchestrator writes prompts, starts workers, audits their commits, and is the only one that flips a phase to done.

## Setup (once)

1. Pull this branch, then copy the folder:
   `docs/chat-orkest/*` → `D:\ware\docs\impl\chat-orkest\` (tracker, `protocol.draft.ts`, this file). Commit in the ware repo.
2. Fix the relative link to HARNESS-PARITY-IMPL.md at the top of the tracker.
3. Paste the HARNESS-PARITY addendum (below) into the existing harness-parity session, or into a fresh GhostDev session.
4. Start the orchestrator session with the orchestrator prompt (below).

## Rules that keep it auditable

- **One worker = one repo path + one phase.** Never two workers in the same repo at once (use a git worktree if you must).
- **Workers tick only their own checklist items** and append one LOGBOOK line: phase, commit hash, test output summary, deviations.
- **Workers end with a report** in a fixed shape (see worker template). No report = not done.
- **The orchestrator audits before marking done:** reads the diff, re-runs the phase's "What Needs Testing" line, runs `/code-review` at medium on the commit range, then flips the status row.
- **Spikes write findings, not production code.** Their output is a research note plus an Open Question resolution.
- **Model choice from the tracker table.** Opus only for CO.1 (contract) and the orchestrator; Sonnet everywhere else. Haiku is fine for pure doc moves.

## Waves

| Wave | Run in parallel | Gate to next wave |
|---|---|---|
| 1 | HARNESS-PARITY.1→5 (GhostDev, sequential inside) · CO.0a (Ghost gateway) · CO.0b (Orkest) · CO.1 (chat, Opus) | CO.0a + CO.0b notes written, Open Q3 resolved, CO.1 fixtures pass |
| 2 | CO.2 (chat) · CO.3 (Orkest) | HARNESS-PARITY.2 done (needed for `packHash`), both phases audited |
| 3 | CO.4 then CO.5 | Methodology check runs end to end with matching cost |
| 4 | CO.6 · CO.7 | — |

---

## Prompt: orchestrator session (Opus, cwd `D:\ware`)

```
You orchestrate two trackers:
- D:\ware\docs\impl\chat-orkest\CHAT-ORKEST-IMPL.md (read fully, plus ORCHESTRATION.md and protocol.draft.ts next to it)
- D:\ware\GhostDev\docs\HARNESS-PARITY-IMPL.md

Your job: start worker sessions (Sonnet unless the tracker table says otherwise), one repo path and one phase each, following the Waves table in ORCHESTRATION.md. Use the worker prompt template from ORCHESTRATION.md, filled in per phase. Do not implement phases yourself.

When a worker reports: read its commit diff, re-run the phase's "What Needs Testing" check, run /code-review medium on the commit range. If clean, flip the phase row to done in the tracker and commit in the ware repo. If not, send the worker a precise fix list. Never let two workers write the same repo at once.

Keep a short status table at the top of your replies: phase, worker, state, blocker. Ask me only for: Open Question overrides, billing/auth decisions (API key vs subscription for harnesses through Ghost), and anything that pushes to a shared remote other than the phase branch.

Start with Wave 1. Tell me which workers you are starting before starting them.
```

## Prompt template: worker session (Sonnet, cwd = the phase repo)

```
Read <tracker path> first: the AI Continuation Prompt, Architecture, Open Questions, and Phase <ID> only. Contract draft: <protocol.draft.ts path or contract/ path>.

Do Phase <ID> in this repo (<repo path>). Scope is exactly the <ID>.* checklist items. Do not touch other phases or other repos. If an item is wrong or blocked, stop and report instead of improvising.

<phase-specific notes from the orchestrator>

Before committing: run the repo's own checks (lint, typecheck, tests, tools/smoke-tools.ps1 where it exists). Commit on branch <branch>. Tick only your items in the tracker, and add one LOGBOOK line.

End with this report and nothing after it:
PHASE: <ID>
COMMITS: <hashes>
CHECKLIST: <items ticked> / <items not ticked + why>
TESTS: <commands run + pass/fail>
DEVIATIONS: <none | list>
OPEN QUESTIONS TOUCHED: <none | which + proposed resolution>
```

---

## Prompt: HARNESS-PARITY addendum (paste into the harness-parity session, or a fresh GhostDev session)

```
Context update for HARNESS-PARITY (GhostDev main, tracker docs/HARNESS-PARITY-IMPL.md at 3d7580a).

A sibling tracker now exists: D:\ware\docs\impl\chat-orkest\CHAT-ORKEST-IMPL.md. Orkest will run Claude Code, Codex and Grok Build as "work threads" and stamp each thread with harness, harness version, and the GhostDev skill pack hash. HARNESS-PARITY stays about skill files only; do not build anything Orkest-side.

Add these to the tracker before running phase 1 (keep the 5-phase shape, add items, no new phase):

1. HARNESS-PARITY.1.2: each registry row gets a stable `name` key: "claude-code", "codex", "grok". Orkest's harness registry reuses these exact names. Document that in the registry file header.
2. New item HARNESS-PARITY.2.5: ghostdev-mark records a single pack hash (hash over template/skills/ in sorted path order, short 12-hex like the existing run-impl hashes) and writes it where a consumer can read it without PowerShell (for example a `pack` field in the mark output JSON). Orkest reads this as `packHash`.
3. HARNESS-PARITY.4.1: the reference doc names the harness `name` keys as canonical ids shared with Orkest, and points at the CHAT-ORKEST tracker.
4. HARNESS-PARITY.5.1: in the Ghost canary, also check that Codex does not warn about or double-list skills that exist in both .agents/skills and .codex/skills. If it does, apply your own Open Question 2 override (drop the .codex row) and record it.

Then run the tracker as planned: phase 1 on Opus/high per its table, the rest on Sonnet. End each phase with: commit hash, ticked items, smoke-tools output, deviations.
```
