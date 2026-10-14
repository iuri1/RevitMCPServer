/**
 * Thin HTTP client to the in-Revit MCP addin.  All MCP tools go through here.
 *
 * Auth: reads the auth token from REVIT_MCP_AUTH_TOKEN env var or from the
 * well-known file at %APPDATA%/Autodesk/Revit/Addins/<version>/revit-mcp-token.txt.
 *
 * On 401 responses the client re-reads the token files (Revit regenerates the
 * token on restart) and retries.  Each Revit year's addin writes its own token
 * into Addins/<year>/, but they all compete for the same port, so the process
 * answering may not be the one REVIT_MCP_VERSION names (e.g. VERSION=2026 while
 * only Revit 2025 is open).  The retry therefore walks every year's token file:
 * configured year first, then the others newest-mtime first, and keeps the one
 * the addin accepts.
 */

import { readFileSync, readdirSync, statSync } from "fs";
import { join } from "path";
import { randomUUID } from "node:crypto";

// The addin's HttpListener is hard-bound to http://127.0.0.1:<port>/, so a
// non-loopback REVIT_MCP_HOST can never reach a real addin — the only thing it
// could do is hand the bearer token and the full command stream to whatever is
// listening there, over plaintext HTTP. Refuse to start instead.
const RAW_HOST = process.env.REVIT_MCP_HOST ?? "127.0.0.1";
const LOOPBACK_HOSTS = new Set(["127.0.0.1", "localhost", "::1", "[::1]"]);
if (!LOOPBACK_HOSTS.has(RAW_HOST)) {
  throw new Error(
    `REVIT_MCP_HOST="${RAW_HOST}" is not a loopback address. The Revit addin ` +
    `only listens on 127.0.0.1, so a remote host can never be a real addin. ` +
    `Remove REVIT_MCP_HOST or set it to 127.0.0.1.`,
  );
}
const REVIT_HOST = RAW_HOST === "::1" ? "[::1]" : RAW_HOST;
const REVIT_VERSION = process.env.REVIT_MCP_VERSION ?? "2026";
// Auto-assign port by version: ≤2026 → 7891, 2027 → 7892, … (same rule as the
// addin's App.ResolvePort).  Explicit REVIT_MCP_PORT always wins.
const DEFAULT_PORT = 7891 + Math.max(0, (parseInt(REVIT_VERSION, 10) || 2026) - 2026);
const REVIT_PORT = Number(process.env.REVIT_MCP_PORT ?? String(DEFAULT_PORT));
const BASE = `http://${REVIT_HOST}:${REVIT_PORT}`;
const TIMEOUT_MS = Number(process.env.REVIT_MCP_TIMEOUT_MS ?? "30000");

export interface RevitEnvelope {
  ok: boolean;
  data?: unknown;
  error?: { code: string; message: string; type?: string };
  // Dry-run responses:
  dryRun?: boolean;
  committed?: boolean;
  // Batch responses also carry these:
  count?: number;
  hadFailures?: boolean;
  results?: unknown;
}

export interface BatchStep {
  command: string;
  params?: Record<string, unknown>;
}

/** Resolve the auth token: explicit env var, then the addin's token file. */
function resolveAuthToken(): string | undefined {
  const explicit = process.env.REVIT_MCP_AUTH_TOKEN;
  if (explicit) return explicit;

  // Configured year's file, or — if that Revit was never started — whichever
  // other year's token is newest.
  return candidateTokens()[0];
}

const addinsRoot = () =>
  join(process.env.APPDATA ?? "", "Autodesk", "Revit", "Addins");
const tokenPathFor = (year: string) =>
  join(addinsRoot(), year, "revit-mcp-token.txt");

/** Read the token from the well-known per-session file written by the addin. */
function readTokenFile(year: string = REVIT_VERSION): string | undefined {
  try {
    return readFileSync(tokenPathFor(year), "utf-8").trim() || undefined;
  } catch {
    return undefined;
  }
}

/**
 * Every distinct token on disk, in the order they should be tried: the
 * configured REVIT_MCP_VERSION first, then the other Addins/<year>/ folders
 * that hold a token file, most recently written first (the Revit started last
 * is the most likely to own the port).
 */
function candidateTokens(): string[] {
  let years: string[] = [];
  try {
    years = readdirSync(addinsRoot()).filter((d) => /^\d{4}$/.test(d));
  } catch {
    // No Addins folder: only the configured year (below) can apply.
  }
  const others = years
    .filter((y) => y !== REVIT_VERSION)
    .map((y) => {
      try {
        return { y, mtime: statSync(tokenPathFor(y)).mtimeMs };
      } catch {
        return undefined; // folder without a token file
      }
    })
    .filter((e): e is { y: string; mtime: number } => e !== undefined)
    .sort((a, b) => b.mtime - a.mtime)
    .map((e) => e.y);

  const tokens: string[] = [];
  for (const y of [REVIT_VERSION, ...others]) {
    const t = readTokenFile(y);
    if (t && !tokens.includes(t)) tokens.push(t);
  }
  return tokens;
}

// Mutable — refreshed automatically on 401 (Revit regenerates token on restart).
let _authToken: string | undefined = resolveAuthToken();

export function hasAuthToken(): boolean {
  return _authToken !== undefined;
}

export function buildHeaders(token: string | undefined = _authToken): Record<string, string> {
  const headers: Record<string, string> = {
    "content-type": "application/json",
  };
  if (token) {
    headers["authorization"] = `Bearer ${token}`;
  }
  return headers;
}

const isUnauthorized = (e: RevitEnvelope) => e.error?.code === "unauthorized";

async function postJson(path: string, body: unknown): Promise<RevitEnvelope> {
  const first = await sendOnce(path, body, _authToken);
  if (!isUnauthorized(first)) return first;

  // 401: the token is stale (Revit restarted) or belongs to another Revit year
  // than the one holding the port.  The addin rejects before executing, so
  // resending is safe.  Try each other token on disk; remember the winner.
  let last = first;
  for (const token of candidateTokens()) {
    if (token === _authToken) continue;
    last = await sendOnce(path, body, token);
    if (!isUnauthorized(last)) {
      _authToken = token;
      return last;
    }
  }
  return last;
}

/** One HTTP round trip to the addin with the given bearer token. */
async function sendOnce(
  path: string,
  body: unknown,
  token: string | undefined,
): Promise<RevitEnvelope> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
  try {
    const headers = buildHeaders(token);
    headers["x-request-id"] = randomUUID();
    const res = await fetch(`${BASE}${path}`, {
      method: "POST",
      headers,
      body: JSON.stringify(body),
      signal: controller.signal,
    });
    const text = await res.text();
    let envelope: RevitEnvelope;
    try {
      envelope = JSON.parse(text) as RevitEnvelope;
    } catch {
      return {
        ok: false,
        error: {
          code: "bad_response",
          message: `Non-JSON response from Revit addin (status ${res.status}): ${text.slice(0, 500)}`,
        },
      };
    }

    return envelope;
  } catch (err) {
    const name = (err as { name?: string })?.name;
    const message = err instanceof Error ? err.message : String(err);
    return {
      ok: false,
      error: {
        code: name === "AbortError" ? "timeout" : "transport_error",
        message: `Failed to reach Revit addin at ${BASE}: ${message}`,
      },
    };
  } finally {
    clearTimeout(timer);
  }
}

export function callRevit(
  command: string,
  params: Record<string, unknown>,
  dryRun = false,
): Promise<RevitEnvelope> {
  const body: Record<string, unknown> = { command, params };
  if (dryRun) body.dryRun = true;
  return postJson("/mcp", body);
}

export function callRevitBatch(
  steps: BatchStep[],
  stopOnError = true,
  dryRun = false,
): Promise<RevitEnvelope> {
  const body: Record<string, unknown> = { stopOnError, steps };
  if (dryRun) body.dryRun = true;
  return postJson("/mcp/batch", body);
}

/** Common helper to convert a Revit envelope into the MCP tool result shape. */
export function envelopeToToolResult(envelope: RevitEnvelope) {
  const text = JSON.stringify(envelope, null, 2);
  return {
    content: [{ type: "text" as const, text }],
    isError: !envelope.ok,
  };
}

export interface HealthInfo {
  reachable: boolean;
  version?: string;
  authEnabled?: boolean;
  authTokenPresent: boolean;
}

/**
 * Probe the addin's /health endpoint.  Never throws — returns a structured
 * result so callers can log diagnostics without crashing startup.
 */
export async function checkRevitHealth(): Promise<HealthInfo> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), 5000);
  try {
    const res = await fetch(`${BASE}/health`, {
      headers: buildHeaders(),
      signal: ctrl.signal,
    });
    if (!res.ok) {
      return { reachable: false, authTokenPresent: hasAuthToken() };
    }
    const data = (await res.json()) as {
      version?: string;
      authEnabled?: boolean;
    };
    return {
      reachable: true,
      version: data.version,
      authEnabled: data.authEnabled,
      authTokenPresent: hasAuthToken(),
    };
  } catch {
    return { reachable: false, authTokenPresent: hasAuthToken() };
  } finally {
    clearTimeout(timer);
  }
}

export const REVIT_BASE_URL = BASE;
