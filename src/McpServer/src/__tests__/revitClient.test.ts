/**
 * Unit tests for revitClient.ts.
 *
 * Because AUTH_TOKEN and BASE_URL are evaluated at module-import time,
 * every test that needs different env values must call vi.resetModules()
 * then dynamically import the module so those constants are re-evaluated.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Helper — build a minimal Response-like mock.
function mockFetch(body: unknown, status = 200) {
  return vi.fn().mockResolvedValueOnce({
    status,
    text: () => Promise.resolve(typeof body === 'string' ? body : JSON.stringify(body)),
  } as unknown as Response);
}

// ── envelopeToToolResult ──────────────────────────────────────────────────────

describe('envelopeToToolResult', () => {
  it('returns isError=false and text content for ok response', async () => {
    const { envelopeToToolResult } = await import('../revitClient.js');
    const result = envelopeToToolResult({ ok: true, data: { foo: 'bar' } });
    expect(result.isError).toBe(false);
    expect(result.content).toHaveLength(1);
    expect(result.content[0].type).toBe('text');
    const parsed = JSON.parse(result.content[0].text);
    expect(parsed.ok).toBe(true);
  });

  it('returns isError=true for error response', async () => {
    const { envelopeToToolResult } = await import('../revitClient.js');
    const result = envelopeToToolResult({
      ok: false,
      error: { code: 'not_found', message: 'missing' },
    });
    expect(result.isError).toBe(true);
  });
});

// ── callRevit body construction ───────────────────────────────────────────────

describe('callRevit', () => {
  beforeEach(() => {
    vi.stubEnv('REVIT_MCP_AUTH_TOKEN', 'test-token');
    vi.resetModules();
  });
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('POST to /mcp with command and params', async () => {
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('ping', { id: 1 });
    const [url, opts] = vi.mocked(global.fetch).mock.calls[0];
    expect(String(url)).toMatch(/\/mcp$/);
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.command).toBe('ping');
    expect(body.params).toEqual({ id: 1 });
  });

  it('includes dryRun=true in body when requested', async () => {
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('create_wall', {}, true);
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.dryRun).toBe(true);
  });

  it('omits dryRun from body when false', async () => {
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('ping', {}, false);
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.dryRun).toBeUndefined();
  });
});

// ── callRevitBatch body construction ──────────────────────────────────────────

describe('callRevitBatch', () => {
  beforeEach(() => {
    vi.stubEnv('REVIT_MCP_AUTH_TOKEN', 'test-token');
    vi.resetModules();
  });
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('POST to /mcp/batch with steps and stopOnError', async () => {
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevitBatch } = await import('../revitClient.js');
    await callRevitBatch([{ command: 'ping', params: {} }]);
    const [url, opts] = vi.mocked(global.fetch).mock.calls[0];
    expect(String(url)).toMatch(/\/mcp\/batch$/);
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.steps).toHaveLength(1);
    expect(body.steps[0].command).toBe('ping');
    expect(body.stopOnError).toBe(true);
  });

  it('passes stopOnError=false when specified', async () => {
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevitBatch } = await import('../revitClient.js');
    await callRevitBatch([{ command: 'ping' }], false);
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.stopOnError).toBe(false);
  });
});

// ── error handling ────────────────────────────────────────────────────────────

describe('postJson error handling', () => {
  beforeEach(() => {
    vi.stubEnv('REVIT_MCP_AUTH_TOKEN', 'test-token');
    vi.resetModules();
  });
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('returns bad_response for non-JSON body', async () => {
    vi.stubGlobal('fetch', mockFetch('not json at all', 200));
    const { callRevit } = await import('../revitClient.js');
    const result = await callRevit('ping', {});
    expect(result.ok).toBe(false);
    expect(result.error?.code).toBe('bad_response');
  });

  it('returns transport_error on network failure', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValueOnce(new Error('ECONNREFUSED')));
    const { callRevit } = await import('../revitClient.js');
    const result = await callRevit('ping', {});
    expect(result.ok).toBe(false);
    expect(result.error?.code).toBe('transport_error');
  });

  it('returns timeout code on AbortError', async () => {
    const err = Object.assign(new Error('The operation was aborted'), { name: 'AbortError' });
    vi.stubGlobal('fetch', vi.fn().mockRejectedValueOnce(err));
    const { callRevit } = await import('../revitClient.js');
    const result = await callRevit('ping', {});
    expect(result.ok).toBe(false);
    expect(result.error?.code).toBe('timeout');
  });
});

// ── auth header ───────────────────────────────────────────────────────────────

describe('auth header', () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('includes Bearer token when REVIT_MCP_AUTH_TOKEN is set', async () => {
    vi.stubEnv('REVIT_MCP_AUTH_TOKEN', 'secret-token-xyz');
    vi.resetModules();
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('ping', {});
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const headers = (opts as RequestInit).headers as Record<string, string>;
    expect(headers['authorization']).toBe('Bearer secret-token-xyz');
  });

  it('ignores REVIT_MCP_AUTH=false — auth cannot be disabled (0.8.17)', async () => {
    vi.stubEnv('REVIT_MCP_AUTH', 'false');
    vi.stubEnv('REVIT_MCP_AUTH_TOKEN', 'still-sent-token');
    vi.resetModules();
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('ping', {});
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const headers = (opts as RequestInit).headers as Record<string, string>;
    expect(headers['authorization']).toBe('Bearer still-sent-token');
  });

  it('always includes content-type header', async () => {
    vi.stubEnv('REVIT_MCP_AUTH_TOKEN', 'test-token');
    vi.resetModules();
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('ping', {});
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const headers = (opts as RequestInit).headers as Record<string, string>;
    expect(headers['content-type']).toBe('application/json');
  });
});

// ── host clamp ────────────────────────────────────────────────────────────────

describe('REVIT_MCP_HOST loopback clamp', () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.resetModules();
  });

  it('refuses to start when REVIT_MCP_HOST is not loopback', async () => {
    vi.stubEnv('REVIT_MCP_HOST', 'evil.example.com');
    vi.resetModules();
    await expect(import('../revitClient.js')).rejects.toThrow(/not a loopback address/);
  });

  it.each(['127.0.0.1', 'localhost', '::1'])('accepts loopback host %s', async (host) => {
    vi.stubEnv('REVIT_MCP_HOST', host);
    vi.stubEnv('REVIT_MCP_AUTH_TOKEN', 'test-token');
    vi.resetModules();
    await expect(import('../revitClient.js')).resolves.toBeDefined();
  });
});

// ── multi-year token fallback ─────────────────────────────────────────────────
// Revit 2025 and 2026 each write their own Addins/<year>/revit-mcp-token.txt but
// share the port; the client must find whichever token the running addin accepts.

describe('token fallback across Revit years', () => {
  let appData: string;

  async function writeToken(year: string, token: string, mtimeSec: number) {
    const { mkdirSync, writeFileSync, utimesSync } = await import('fs');
    const { join } = await import('path');
    const dir = join(appData, 'Autodesk', 'Revit', 'Addins', year);
    mkdirSync(dir, { recursive: true });
    const file = join(dir, 'revit-mcp-token.txt');
    writeFileSync(file, token + '\r\n');
    utimesSync(file, mtimeSec, mtimeSec);
  }

  /** fetch mock: 200 only for `goodToken`, 401 envelope otherwise. */
  function addinAccepting(goodToken: string) {
    return vi.fn(async (_url: unknown, opts: RequestInit) => {
      const auth = (opts.headers as Record<string, string>)['authorization'];
      const ok = auth === `Bearer ${goodToken}`;
      const body = ok
        ? { ok: true, data: 'pong' }
        : { ok: false, error: { code: 'unauthorized', message: 'bad token' } };
      return { status: ok ? 200 : 401, text: async () => JSON.stringify(body) } as unknown as Response;
    });
  }

  const sentTokens = (f: ReturnType<typeof vi.fn>) =>
    f.mock.calls.map(([, o]) => ((o as RequestInit).headers as Record<string, string>)['authorization']);

  beforeEach(async () => {
    const { mkdtempSync } = await import('fs');
    const { tmpdir } = await import('os');
    const { join } = await import('path');
    appData = mkdtempSync(join(tmpdir(), 'revit-mcp-test-'));
    vi.stubEnv('APPDATA', appData);
    vi.stubEnv('REVIT_MCP_AUTH_TOKEN', '');
    vi.resetModules();
  });
  afterEach(async () => {
    const { rmSync } = await import('fs');
    rmSync(appData, { recursive: true, force: true });
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('VERSION=2026 but only Revit 2025 has a token: starts with the 2025 token', async () => {
    vi.stubEnv('REVIT_MCP_VERSION', '2026');
    await writeToken('2025', 'tok-2025', 1000);
    const f = addinAccepting('tok-2025');
    vi.stubGlobal('fetch', f);
    const { callRevit, hasAuthToken } = await import('../revitClient.js');
    expect(hasAuthToken()).toBe(true);
    const r = await callRevit('ping', {});
    expect(r.ok).toBe(true);
    expect(sentTokens(f)).toEqual(['Bearer tok-2025']);
  });

  it('on 401 tries the other years (configured first, then newest) and remembers the winner', async () => {
    vi.stubEnv('REVIT_MCP_VERSION', '2026');
    await writeToken('2026', 'tok-2026', 3000); // stale: Revit 2026 closed
    await writeToken('2027', 'tok-2027', 1000); // older
    await writeToken('2025', 'tok-2025', 2000); // newer than 2027 -> tried before it
    const f = addinAccepting('tok-2025');
    vi.stubGlobal('fetch', f);
    const { callRevit } = await import('../revitClient.js');

    const r1 = await callRevit('ping', {});
    expect(r1.ok).toBe(true);
    expect(sentTokens(f)).toEqual(['Bearer tok-2026', 'Bearer tok-2025']);

    // Second call goes straight to the token that worked.
    f.mockClear();
    const r2 = await callRevit('ping', {});
    expect(r2.ok).toBe(true);
    expect(sentTokens(f)).toEqual(['Bearer tok-2025']);
  });

  it('returns unauthorized after every token on disk was rejected', async () => {
    vi.stubEnv('REVIT_MCP_VERSION', '2026');
    await writeToken('2026', 'tok-2026', 2000);
    await writeToken('2025', 'tok-2025', 1000);
    const f = addinAccepting('nobody-has-this');
    vi.stubGlobal('fetch', f);
    const { callRevit } = await import('../revitClient.js');
    const r = await callRevit('ping', {});
    expect(r.error?.code).toBe('unauthorized');
    expect(sentTokens(f)).toEqual(['Bearer tok-2026', 'Bearer tok-2025']);
  });

  it('a stale REVIT_MCP_AUTH_TOKEN still falls back to the token files', async () => {
    vi.stubEnv('REVIT_MCP_AUTH_TOKEN', 'env-token');
    await writeToken('2025', 'tok-2025', 1000);
    const f = addinAccepting('tok-2025');
    vi.stubGlobal('fetch', f);
    const { callRevit } = await import('../revitClient.js');
    const r = await callRevit('ping', {});
    expect(r.ok).toBe(true);
    expect(sentTokens(f)).toEqual(['Bearer env-token', 'Bearer tok-2025']);
  });
});
