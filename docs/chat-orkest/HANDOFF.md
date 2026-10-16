# HANDOFF — CHAT-ORKEST + HARNESS-PARITY big run

> Written 2026-10-16 from a cloud planning session. Read this first, then [GROK-RUN.md](GROK-RUN.md) (the prompt) and [CHAT-ORKEST-IMPL.md](CHAT-ORKEST-IMPL.md) (the tracker).
> STATUS: PLAN-ONLY. No code written in any repo yet.

## What was decided

1. **Own chat app (React).** assistant-ui + AI SDK v7, new repo `D:\ware\chat` (`contract/`, `app/`, `embed/`). Botanic/LibreChat stays the unmodified image; no bridge between their stores.
2. **Four layers, one question each.** Skill pack = GhostDev (what the agent knows). Harness adapter = Orkest/GhostShell (start, watch, pause). Gateway = Ghost (who pays). Thread store + UI = chat app (what happened).
3. **One data model.** Thread → Turn → Item (Codex shape). `chat` threads run against Ghost from the chat server; `work` threads run in a path through Orkest. Draft types: [protocol.draft.ts](protocol.draft.ts).
4. **Orkest dials out** to the chat app over WebSocket. The chat app never reaches into a machine.
5. **ACP first** between Orkest and harnesses (Claude Code, Codex, Grok Build); native fallback per harness if the spike says so.
6. **Vue embedding without vuera.** Iframe `/t/{id}?embed=1` for the full view; `<ware-thread>` custom element for inline panels.
7. **Gateway stays LiteLLM + Ghost policy layer.** No base switch. Thread tagging uses LiteLLM's own headers: `x-litellm-trace-id` = thread id, `x-litellm-spend-logs-metadata` = `{turn, harness, machine, source}`. See [AI-GATEWAY-REVIEW.md](AI-GATEWAY-REVIEW.md).
8. **Gateway work in this run:** items 1 (tagging), 6 (`orkest-<machine>` keys) in CO.0a; item 2 (ledger API) with CO.2; item 7 (capabilities) with CO.4. Items 4, 5, 8 are Ghost backlog, **not** in this run.
9. **HARNESS-PARITY** runs as written in GhostDev plus the addendum (stable harness `name` keys, pack hash readable without PowerShell, Codex double-listing check). It is independent and starts first.

## Default taken for the one open decision

**D1 — billing for Claude Code work threads: API-key lane through Ghost.** Every work thread is proxied, one ledger, no reported-usage endpoint needed in this run (item 3 stays designed-only in the CO.0a note).
> Override: keep Claude Code on the Max login and build `POST /ledger/usage` (reported usage) in CO.2. Say so to the orchestrator before CO.0a starts. Never route work threads through `ghost-claude-broker` either way.

## Repos involved

| Repo | Path | Phases |
|---|---|---|
| ware (umbrella docs) | `D:\ware` | trackers live in `docs\impl\chat-orkest\` |
| GhostDev | `D:\ware\GhostDev` | HARNESS-PARITY.1–5 |
| Ghost (gateway + GhostShell) | `D:\ware\Ghost` | CO.0a, ledger/capabilities items; probably also Orkest (CO.0b, CO.3+) — the orchestrator confirms where GhostShell/Orkest code lives in step 0 |
| chat (new) | `D:\ware\chat` | CO.1, CO.2, CO.5, CO.6 |
| botanic | `D:\ware\botanic` | read-only reference (research notes, gaps) |

If gateway and Orkest are the same repo, their phases use separate git worktrees so two workers never share a working tree.

## Known traps

- **Grok + stale skills.** Grok Build reads `.grok/skills`, then `.agents/skills`, then `.claude/skills`. Ghost and Rompe still have the stale `.agents/skills/run-impl` (`BC4F708ECEDA`) that demands `--force` on a dirty tree. Until HARNESS-PARITY.5 lands, the orchestrator must not invoke `/run-impl` inside Ghost or Rompe; workers follow the tracker text instead.
- **Max broker.** Single-flight, 60 s gap, no tools, no streaming. Never an upstream for a work thread or a harness.
- **LiteLLM pinning.** PyPI 1.82.7/1.82.8 were backdoored (March 2026). Do not bump LiteLLM in this run.
- **LibreChat `non_browser` ban.** Scripts hitting a LibreChat instance get banned for 120 min. Nothing in this run should call LibreChat.
- **No `docker compose down -v`** on any Botanic or Ghost stack.

## Files in this folder

| File | Purpose |
|---|---|
| `HANDOFF.md` | This file. |
| `GROK-RUN.md` | The orchestrator prompt for Grok Build at `D:\ware`. |
| `CHAT-ORKEST-IMPL.md` | Tracker: architecture, phases, checklists. |
| `ORCHESTRATION.md` | Rules, waves, worker prompt template, HARNESS-PARITY addendum. |
| `AI-GATEWAY-REVIEW.md` | Gateway verdict, alternatives, improvement list. |
| `protocol.draft.ts` | Draft wire contract for CO.1. |
