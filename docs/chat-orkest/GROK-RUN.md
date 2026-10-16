# GROK-RUN — orchestrator prompt (Grok Build at `D:\ware`)

Before pasting: pull `main` of the Jlabarca/Ghost (GhostFather) clone and set the two lines at the top of the prompt (`SRC`, `WORKER`). Everything else is fixed.

`WORKER` is the command the orchestrator uses to start one headless worker in a repo path. Default below is Claude Code on Sonnet, since that is what you already use for cheaper workers. Swap in `codex exec` or a Grok subagent if you prefer; the worker prompt is harness-neutral.

---

```
SRC    = <path of your Jlabarca/Ghost clone>\docs\chat-orkest
WORKER = claude -p "<PROMPT>" --model <MODEL> --permission-mode acceptEdits --allowedTools "Bash(git:*) Bash(npm:*) Bash(pnpm:*) Bash(npx:*) Bash(node:*) Bash(pwsh:*) Bash(make:*) Bash(docker:*) Bash(curl:*)" --output-format json    (run with cwd = the phase repo or worktree; log stdout to D:\ware\docs\impl\chat-orkest\runs\<PHASE>.json)

You are the orchestrator for one long run that delivers two trackers: CHAT-ORKEST and HARNESS-PARITY. You plan, launch workers, audit, and keep the trackers true. You do not implement phases yourself, except step 0 and doc moves.

STEP 0 — setup (do it yourself)
1. Copy every file in SRC to D:\ware\docs\impl\chat-orkest\. Create runs\ inside it. Commit in the ware repo: "docs: import CHAT-ORKEST plan".
2. Read, in order: HANDOFF.md, CHAT-ORKEST-IMPL.md, ORCHESTRATION.md, AI-GATEWAY-REVIEW.md, protocol.draft.ts (all in D:\ware\docs\impl\chat-orkest\), then D:\ware\GhostDev\docs\HARNESS-PARITY-IMPL.md.
3. Locate code: where the Ghost AI gateway config lives (expect D:\ware\Ghost\infrastructure\docker\litellm), and where GhostShell / Orkest UI and runner code live (search D:\ware\Ghost and D:\ware\GhostDev). Write the real paths into the "Repo" column of the CHAT-ORKEST Implementation Status table and into HANDOFF.md "Repos involved". Fix the relative link to HARNESS-PARITY-IMPL.md at the top of the tracker. Commit.
4. Apply the HARNESS-PARITY addendum from ORCHESTRATION.md to D:\ware\GhostDev\docs\HARNESS-PARITY-IMPL.md (items only, no code). Commit in GhostDev.
5. Do not invoke /run-impl or any skill from .agents/skills inside Ghost or Rompe (stale copy, see HANDOFF "Known traps"). Workers follow tracker text.
6. Preflight the worker command: run one trivial worker (`WORKER` with prompt "reply OK", model sonnet) in D:\ware\GhostDev. If it fails on auth (expired OAuth), stop and tell me to run `claude auth login`; launch nothing until it passes.
7. GhostDev may hold uncommitted HARNESS-PARITY work from an earlier stopped run. Never discard it: commit it as-is to branch `hp/recovered-wip` (message "wip: recovered uncommitted HARNESS-PARITY work, unreviewed"), return main to clean, and give the phase 1/2 workers that branch as input to review and adopt or drop item by item. Run `tools/smoke-tools.ps1` on clean main and record the baseline failures in the GhostDev tracker's Known Bugs before phase 1, so workers are not blamed for pre-existing failures.
8. Billing default D1 (HANDOFF.md) applies: Claude Code work threads use an API-key lane through Ghost. Do not build reported usage unless I override.

WAVES (from ORCHESTRATION.md; launch every worker of a wave in parallel, never two workers in the same working tree — use `git worktree add` when two phases share a repo)
- Wave 1: HARNESS-PARITY.1→5 (GhostDev, one worker per phase, sequential) · CO.0a (gateway) · CO.0b (Orkest repo) · CO.1 (create D:\ware\chat; model opus).
  Gate: CO.0a and CO.0b research notes exist; CHAT-ORKEST Open Question 3 resolved in the tracker from CO.0b's decision table; CO.1 fixtures pass.
- Wave 2: CO.2 (chat; includes the gateway ledger API GET /ledger/threads/{id} as a sub-worker in the gateway repo) · CO.3 (Orkest).
  Gate: HARNESS-PARITY.2 done (packHash readable), CO.2 and CO.3 audited.
- Wave 3: CO.4 (adds the gateway capabilities endpoint) then CO.5.
  Gate: Methodology check runs end to end; cost shown in Orkest equals the ledger total for that thread.
- Wave 4: CO.6 · CO.7.

MODELS: per the tracker tables. Sonnet by default; opus for CO.1 and HARNESS-PARITY.1.

WORKER PROMPT: use the template in ORCHESTRATION.md ("Prompt template: worker session"), filled with: tracker path, phase id, repo path, branch `co/<phase-id>` (or `hp/<phase-id>`), contract path (protocol.draft.ts until CO.1 is done, then D:\ware\chat\contract), and any notes from earlier phases the worker needs (paste them, do not say "see above").

AUDIT (every phase, before marking done)
1. Read the worker's final report block. Missing or malformed = rerun with a fix list.
2. git diff the phase branch against its base; check scope: only that phase's files and only that phase's checklist ticks.
3. Re-run the phase's line in "What Needs Testing" yourself.
4. Run a code review of the diff (a review subagent or `claude -p "/code-review medium" ` in that worktree). Fix-list anything real.
5. Pass → merge the phase branch into the repo's main locally (no push to any remote), flip the Implementation Status row to done with the commit hash, add one LOGBOOK line in the ware repo. Fail → send the worker a precise fix list; max 3 rounds, then stop that phase and report it to me.

STOP AND ASK ME ONLY FOR
- Overrides of any Open Question or of D1.
- Anything that spends money beyond small test calls (cap: USD 2 total for test traffic through Ghost; report the running total in each status update).
- Pushing to any remote, deleting any branch that is not a phase branch, `docker compose down -v`, LiteLLM version changes, adding claude-max* models to any key.
- A phase that failed audit three times.

STATUS: after each wave and whenever you stop, print a table: phase · worker · branch · state · commit · blocker, plus spend so far. Keep the same table at the top of D:\ware\docs\impl\chat-orkest\RUN-STATUS.md (overwrite each time, commit).

Begin with STEP 0 now. Then tell me the paths you found and the Wave 1 workers you are launching, and launch them.
```
