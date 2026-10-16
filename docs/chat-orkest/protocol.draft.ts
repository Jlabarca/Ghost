// Draft wire contract for CHAT-ORKEST. Starting point for CO.1 (port to Zod in D:\ware\chat\contract).
// Plain types only, no runtime. Field names are proposals; CO.0a/CO.0b results may change usage fields.

export type ThreadKind = "chat" | "work";
export type ThreadStatus = "idle" | "running" | "waiting_approval" | "done" | "failed" | "interrupted";
export type HarnessName = "claude-code" | "codex" | "grok"; // must match GhostDev template/harness-registry.json
export type Level = "cheap" | "medium" | "high" | "max";

export interface Thread {
  id: string;
  kind: ThreadKind;
  account: string;
  title: string;
  status: ThreadStatus;
  version: number; // bumped on every state change; approvals carry it as expectedVersion
  createdAt: string;
  work?: {
    machine: string;
    cwd: string;
    harness: HarnessName;
    harnessVersion: string;
    packHash: string; // GhostDev skill pack revision at thread start
    level: Level;
  };
  parentThreadId?: string; // chat → work handoff (CO.7.3)
}

export interface Usage {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens?: number;
  costUsd?: number; // from Ghost, authoritative
}

export interface Turn {
  id: string;
  threadId: string;
  input: string;
  status: "running" | "done" | "failed" | "interrupted";
  usage?: Usage;
  startedAt: string;
  endedAt?: string;
}

interface ItemBase {
  id: string;
  turnId: string;
  status: "in_progress" | "completed" | "failed";
}

export type Item =
  | (ItemBase & { type: "message"; role: "user" | "assistant"; text: string })
  | (ItemBase & { type: "reasoning"; text: string })
  | (ItemBase & { type: "tool_call"; name: string; args: unknown; result?: unknown })
  | (ItemBase & { type: "command"; cmd: string; cwd: string; output: string; exitCode?: number })
  | (ItemBase & { type: "file_change"; path: string; diff: string; applied: boolean })
  | (ItemBase & { type: "approval_request"; approvalId: string; summary: string; options: ("allow" | "allow_always" | "deny")[]; expectedVersion: number; decision?: "allow" | "allow_always" | "deny" })
  | (ItemBase & { type: "artifact"; mime: string; uri: string; title?: string })
  | (ItemBase & { type: "error"; message: string });

export type ToolPolicy = "read-only" | "ask" | "auto-edit";

// Orkest → chat app
export type OrkestToChat =
  | { type: "hello"; machine: string; roots: string[]; harnesses: { name: HarnessName; version: string; packHash: string }[] }
  | { type: "item/started"; threadId: string; item: Item }
  | { type: "item/delta"; threadId: string; itemId: string; textDelta?: string; outputDelta?: string }
  | { type: "item/completed"; threadId: string; item: Item }
  | { type: "approval/request"; threadId: string; item: Extract<Item, { type: "approval_request" }> }
  | { type: "turn/completed"; threadId: string; turnId: string; status: Turn["status"]; usage?: Usage }
  | { type: "thread/status"; threadId: string; status: ThreadStatus; version: number };

// chat app → Orkest
export type ChatToOrkest =
  | { type: "thread/start"; threadId: string; cwd: string; harness: HarnessName; level: Level; toolPolicy: ToolPolicy; prompt?: string; turnId?: string }
  | { type: "turn/start"; threadId: string; turnId: string; text: string }
  | { type: "turn/steer"; threadId: string; turnId: string; text: string }
  | { type: "turn/interrupt"; threadId: string; turnId: string }
  | { type: "approval/resolve"; threadId: string; approvalId: string; decision: "allow" | "allow_always" | "deny"; expectedVersion: number };

// iframe embed (chat app page ↔ Orkest parent window)
export type EmbedMessage =
  | { type: "open-thread"; threadId: string }
  | { type: "thread-status"; threadId: string; status: ThreadStatus }
  | { type: "approval-needed"; threadId: string; approvalId: string };

// Orkest harness registry row
export interface HarnessRow {
  name: HarnessName;
  launch: string[];
  protocol: "acp" | "claude-stream-json" | "codex-app-server";
  fallback?: { launch: string[]; protocol: HarnessRow["protocol"] };
  gatewayEnv: Record<string, string>; // templated: {ghost} {thread} {meta}; thread → x-litellm-trace-id, meta → x-litellm-spend-logs-metadata
  skillRoots: string[]; // informational; GhostDev owns projection
  approvals: boolean;
}
