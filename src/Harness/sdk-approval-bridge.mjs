// Fallback only: the pinned sdk-jsonrpc-server (0.2.1-rc.5) has no
// `approvalRequests` bridge, but the editor's write approval now travels the
// loopback TCP bridge (`approval` operation) instead of stdout, so this plugin
// is not mounted by sdk-composition.mjs. It stays available for a future
// composition that chooses SDK-native approvals; loading it would create a
// second approval prompt.
// The plugin turns the `approval/request` waterfall into a server-to-client
// JSON-RPC request on stdout and resolves the outcome from the client's reply.
const ID_PREFIX = 'qingye-approval-';
const OUTCOMES = new Set(['allowed-once', 'rejected', 'cancelled']);

const pending = new Map();
let nextId = 0;
let reading = false;

function requestClient(parameters, signal) {
  return new Promise(resolve => {
    if (signal?.aborted) return resolve('cancelled');
    const id = `${ID_PREFIX}${++nextId}`;
    let settled = false;
    const finish = outcome => {
      if (settled) return;
      settled = true;
      pending.delete(id);
      signal?.removeEventListener('abort', onAbort);
      resolve(outcome);
    };
    const onAbort = () => finish('cancelled');
    signal?.addEventListener('abort', onAbort, { once: true });
    pending.set(id, finish);
    try {
      process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id, method: 'approval/request', params: parameters })}\n`);
    } catch {
      finish('unavailable');
    }
  });
}

function readReplies() {
  if (reading) return;
  reading = true;
  let buffer = '';
  process.stdin.on('data', chunk => {
    buffer += chunk.toString('utf8');
    let end;
    while ((end = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, end).trim();
      buffer = buffer.slice(end + 1);
      if (!line) continue;
      let frame;
      try { frame = JSON.parse(line); } catch { continue; }
      if (!frame || typeof frame !== 'object' || frame.method !== undefined) continue;
      const finish = pending.get(frame.id);
      if (!finish) continue;
      const outcome = frame.error ? 'unavailable' : frame.result?.outcome;
      finish(OUTCOMES.has(outcome) ? outcome : 'unavailable');
    }
  });
}

export const name = 'qingye-sdk-approval-bridge';
export const inject = ['approval'];

export function apply(ctx, config = {}) {
  if (config.enabled !== true) return;
  readReplies();
  ctx.effect(() => ctx.on('approval/request', async (request, next) => {
    const parameters = {
      sessionId: String(request.agent.session.id),
      toolName: request.toolName,
      ...(request.callId === undefined ? {} : { callId: request.callId }),
      ...(request.reason === undefined ? {} : { reason: request.reason }),
    };
    return requestClient(parameters, request.signal);
  }));
}
