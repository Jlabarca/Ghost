# Ghost AI gateway — is it good enough, and what to improve

> Study 2026-10-16. Inputs: the botanic repo (`README.md`, `docs/gaps.md`, `docs/upstream.md`, `docs/spike-p0.md`, `librechat.yaml`), public LiteLLM / Bifrost docs, and gateway comparison articles. The gateway repo itself (`waremoto/ghost`, `infrastructure/docker/litellm/`) was **not read** in this session; everything about its internals comes from botanic's description. Verify items marked (verify) against the gateway repo.
> Companion to [CHAT-ORKEST-IMPL.md](CHAT-ORKEST-IMPL.md).

## What Ghost's AI gateway is today

It is not a gateway written from scratch. It is **LiteLLM 1.85 (open-source proxy) plus a Ghost policy layer** around it, run from the Ghost repo (`make dev-ai-gateway`, service `litellm:4000` on the `docker_ghost` network).

| Piece | What it does | Where |
|---|---|---|
| LiteLLM proxy | One OpenAI-compatible door; provider translation, fallback chains, spend logs, virtual keys | `infrastructure/docker/litellm` |
| `consumers.json` | One key per consumer (`botanic-default`, `botanic-paid`, `botanic-admin`, `botanic-embed`…) with an allowed-model list. That list is the fence | Ghost |
| Lanes | Fixed model names (`botanic-recommended`, `botanic-free`, `cheap`, `smart`, `botanic-admin-*`) whose upstream order lives on the gateway; admin lanes switchable live in GhostShell > Botanic > Lanes | gateway DB models |
| Budget script | Per-end-user monthly reserve, applied every 300 s (per-model budgets are a paid LiteLLM feature) | `make botanic-budget` |
| `ghost-claude-broker` | Puts the operator's Claude Max subscription behind a model name. Single-flight, 60 s gap, no streaming, no function calling, 2000 msgs/week. Operator-only by Anthropic's consumer terms | Ghost |
| Local models | Ollama (qwen3 8b), speaches (STT/TTS) | `infrastructure/docker/local-llm` |
| Search | SearXNG (`ghost-search`) | Ghost |
| Tracing | Phoenix gets every prompt and answer | lab1, local only |
| Fence tests | `smoke.sh` asserts public keys can't reach Max and the admin key can't reach public lanes | `make ai-gateway-smoke` |

Upstreams behind it: Anthropic API, OpenRouter (Gemini flash-lite), local Ollama, the Max broker.

## "Our OpenRouter"? Half right

| | OpenRouter | Ghost gateway |
|---|---|---|
| One key, many models | yes | yes |
| Fallback / routing | yes (provider routing) | yes (lane chains) |
| Pays providers for you, sells credits | **yes** | no: you hold provider keys; Ghost *tracks* cost against reserves |
| Your own local models | no | **yes** (Ollama, speaches) |
| Subscription as a backend (Claude Max) | no | **yes** (broker) |
| Policy fences per consumer, per end user | basic | **yes** (consumers.json + smoke) |
| Knows your sessions / threads | no | not yet (see below) |
| Runs where your data is | no | yes (lab1) |

So: **Ghost is your OpenRouter + your Portkey/Helicone-lite + your local model host**, and it *uses* OpenRouter as one of its upstreams. That layering is right: OpenRouter is a cheap way to reach many paid models; Ghost owns policy, cost attribution and anything private or local.

## Is it good enough for CHAT-ORKEST?

| Need | Verdict | Notes |
|---|---|---|
| Chat app `/api/chat` (OpenAI-compatible) | **yes** | Botanic already does this every day. |
| Claude Code through the gateway (`/v1/messages`) | **yes, documented** | LiteLLM's Claude Code guide: `ANTHROPIC_BASE_URL` + `ANTHROPIC_AUTH_TOKEN` = virtual key. Works with any lane that supports tool calls. Not with the Max broker (no tool calling, no streaming). |
| Codex (`/v1/responses`) | **probably** (verify) | LiteLLM has a Responses API; a Codex-CLI-to-LiteLLM setup was not found in docs. Spike it. |
| Grok Build (xAI) | **probably** (verify) | xAI is a LiteLLM provider; how Grok Build sets its base URL is unknown. |
| Group calls by thread | **yes, free** | `x-litellm-trace-id: <threadId>` is stored in `LiteLLM_SpendLogs.session_id` and shows in the UI Session Logs. Use it instead of inventing `x-ware-thread`. |
| Tag turn / harness / machine | **yes, free** | `x-litellm-spend-logs-metadata: {"turn":"…","harness":"…","machine":"…"}` lands in the spend row. |
| Per-end-user attribution | **yes** | Already used (`botanic-admin:<id>`). |
| Claude Code on the Max subscription | **no** | The broker can't serve an agent. Either an API-key lane for work threads, or Claude Code on its own login with usage *reported* to Ghost (below). Billing decision for the operator. |
| Spend report the chat app can read | **partial** | LiteLLM has spend endpoints; there's no Ghost-shaped "cost of thread X" API yet. |
| Budget that degrades instead of closing the lane | **no** | `gaps.md` #2. Paid feature, or a pre-call hook. |
| Prompt privacy per consumer | **weak** | Spend logs keep prompts; the redaction header crashes on fallback (`gaps.md`). |

**Verdict: keep it.** The provider and protocol layer (LiteLLM) covers every hop CHAT-ORKEST needs, and thread grouping is already built in. The gaps are all in **Ghost's own policy layer**, which is the part that is tailored to you anyway. Replacing LiteLLM would not close them.

## Open-source alternatives, for perspective

| Project | Stack | Strong at | Why not switch now |
|---|---|---|---|
| **LiteLLM** (current) | Python | Widest provider coverage, `/v1/messages` + Responses, spend logs, sessions | Several useful controls are paid (per-model budgets, disabling callbacks per request). Supply-chain incident on PyPI 1.82.7/1.82.8 (March 2026): keep pinning by digest. |
| **Bifrost** (maximhq) | Go | Very low overhead, virtual keys with customer/team/key budgets enforced in real time, `/anthropic` route for Claude Code, MCP gateway | The best candidate if LiteLLM's paywalled budget features keep biting. Most claims come from its vendor; a migration also means re-doing fences, lanes and smoke. |
| **Portkey Gateway** | TypeScript | Config-driven fallbacks, retries, guardrails, PII redaction | Persistent ledger and governance were historically in its hosted control plane; check what the 2026 open release includes. |
| **Helicone** (+ Rust ai-gateway) | Rust / TS | Observability-first; sessions as a first-class concept | Overlaps Phoenix plus LiteLLM spend logs. A logging layer more than a policy gate. |
| **TensorZero** | Rust | Gateway, episodes, experimentation | Reported archived in June 2026. Skip. |
| **new-api / one-api** | Go | "Your own OpenRouter" literally: user quotas, top-ups, channel pooling, format conversion | Built for reselling access. Not checked in this study. |
| Kong / Envoy / APISIX AI plugins | infra | Enterprise API management | Too heavy for one operator. |

Rule of thumb: **switch base only if a missing feature can't be built as a LiteLLM hook.** Per-model budgets and degrade-on-spend can be built as one (LiteLLM supports custom pre-call hooks and custom loggers in the open-source proxy (verify on 1.85)). So nothing found here forces a switch.

## How to improve it (tailored, in order)

1. **Thread tagging contract (CO.0a).** Standardize on `x-litellm-trace-id` = thread id and `x-litellm-spend-logs-metadata` = `{turn, harness, machine, source}`. Orkest and the chat app set them; nothing new on the gateway. Add a `smoke.sh` check that a tagged call lands with `session_id` set.
2. **A ledger API (Ghost-shaped spend).** A small read service over `LiteLLM_SpendLogs`: `GET /ledger/threads/{id}` (turns, tokens, cost, requested vs served model, fallbacks hit), `GET /ledger/accounts/{id}?period=`. The chat app and Orkest read cost from here, never from LiteLLM internals. This is also the fix for "show which model answered" (`gaps.md` #1) in your own chat app.
3. **Reported usage.** `POST /ledger/usage` for runs that don't proxy through Ghost (Claude Code on its own Max login, any harness on a direct key). Orkest posts the harness's final usage/cost event, marked `source: reported` vs `proxied`. One ledger even when traffic can't be routed.
4. **Policy as a LiteLLM hook, not a cron.** A pre-call hook that checks the end user's reserve per lane and **rewrites the model to the free lane** when spent (`gaps.md` #2 and #3), and returns a clear error otherwise. It replaces the 300 s budget script and its new-user gap.
5. **Privacy in the hook too.** Do redaction in Ghost's own logger per consumer instead of the header that crashes on fallback. Keep prompts only for consumers that opt in (operator, debugging).
6. **Work-thread consumers.** New keys per Orkest machine (`orkest-<machine>`) with lanes made for agents: tool-calling-capable models only, and never the Max broker. Smoke asserts the broker is unreachable from them.
7. **Capabilities endpoint.** `GET /ghost/capabilities` for a key: lanes, protocols (chat, messages, responses), tool support, streaming, cost tier. The chat app's model picker and Orkest's `level → model` map read it instead of hard-coding.
8. **Hygiene from `gaps.md`.** A dedicated network for gateway consumers; keep LiteLLM pinned by digest given the PyPI incident.

Items 1, 3 and 6 are what CHAT-ORKEST needs first. 2 comes with CO.2 (cost in the UI). 4, 5, 7 and 8 are Ghost's own backlog.

## Sources

- botanic repo: `README.md`, `docs/gaps.md`, `docs/upstream.md`, `docs/spike-p0.md`
- LiteLLM request headers (`x-litellm-trace-id` → `session_id`, `x-litellm-spend-logs-metadata`): https://docs.litellm.ai/docs/proxy/request_headers
- LiteLLM session logs: https://docs.litellm.ai/docs/proxy/ui_logs_sessions
- LiteLLM Claude Code setup: https://docs.litellm.ai/docs/proxy/client_setup/claude_code
- Claude Code LLM gateway docs: https://code.claude.com/docs/en/llm-gateway.md
- Bifrost Claude Code gateway (vendor): https://getbifrost.ai/guides/claude-code/claude-code-gateway
- Gateway comparisons (opinion, mixed incentives): https://techsy.io/en/blog/best-llm-gateway-tools · https://www.getmaxim.ai/articles/tensorzero-alternatives-5-llm-gateways-compared-in-2026/
