import net from 'node:net';

// The native bridge is the one contract between the editor process and the SDK
// plugins: `bridge(operation, payload, signal)` resolves the editor's answer.
// The legacy WebView launcher installs it over QINGYE stdio lines; the SDK
// launcher installs the TCP client below. Plugins never know the transport.
const BRIDGE = Symbol.for('qingye.nativeBridge');

/** Install the process-wide native bridge implementation. */
export function installNativeBridge(bridge) {
  if (typeof bridge !== 'function') throw new TypeError('native bridge 必须是函数');
  globalThis[BRIDGE] = bridge;
}

/** The installed bridge; throws when the runtime was started outside the editor. */
export function getNativeBridge() {
  const bridge = globalThis[BRIDGE];
  if (typeof bridge !== 'function') throw new Error('需要由轻页启动此插件');
  return bridge;
}

export function bridgeInstalled() {
  return typeof globalThis[BRIDGE] === 'function';
}

/**
 * TCP client for the editor-owned loopback bridge. The editor is the server:
 * this client sends `{"token":...}` as its first line, then one JSON request
 * per line and matches `{id, value|error}` replies.
 * @param {{port: number|string, token?: string, host?: string, connectTimeoutMs?: number, requestTimeoutMs?: number, timeouts?: Record<string, number>, log?: (message: string) => void}} options
 * @returns {(operation: string, payload: object, signal?: AbortSignal) => Promise<unknown>} the bridge.
 */
export function createTcpBridge(options) {
  const host = options.host ?? '127.0.0.1';
  const port = Number(options.port);
  if (!Number.isInteger(port) || port <= 0 || port > 65535) throw new Error('QINGYE_BRIDGE_PORT 无效');
  const token = options.token ?? '';
  const connectTimeoutMs = options.connectTimeoutMs ?? 10_000;
  const requestTimeoutMs = options.requestTimeoutMs ?? 120_000;
  const timeouts = options.timeouts ?? {};
  const timeoutFor = operation => {
    const value = Number(timeouts[operation]);
    return Number.isFinite(value) && value > 0 ? value : requestTimeoutMs;
  };
  const log = options.log ?? (() => {});

  let socket;
  let connecting;
  let buffer = '';
  let nextId = 0;
  let closed = false;
  const pending = new Map();
  const abandoned = new Set();

  const rejectAll = error => {
    for (const request of pending.values()) {
      request.cleanup();
      request.reject(error);
    }
    pending.clear();
  };

  const reset = () => {
    socket = undefined;
    connecting = undefined;
    buffer = '';
    rejectAll(new Error('本机桥连接已断开'));
  };

  const settle = message => {
    const request = pending.get(message.id);
    if (!request) { abandoned.delete(message.id); return; }
    pending.delete(message.id);
    request.cleanup();
    if (message.error !== undefined && message.error !== null) request.reject(new Error(String(message.error)));
    else request.resolve(message.value);
  };

  const onData = chunk => {
    buffer += chunk.toString('utf8');
    let end;
    while ((end = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, end).trim();
      buffer = buffer.slice(end + 1);
      if (!line) continue;
      let message;
      try { message = JSON.parse(line); } catch { log(`本机桥收到无法解析的行：${line.slice(0, 200)}`); continue; }
      if (message && typeof message === 'object' && message.id !== undefined && ('value' in message || 'error' in message)) settle(message);
    }
  };

  const connect = () => {
    if (closed) return Promise.reject(new Error('本机桥已关闭'));
    if (socket && !socket.destroyed) return Promise.resolve();
    if (connecting) return connecting;
    const deadline = Date.now() + connectTimeoutMs;
    connecting = new Promise((resolve, reject) => {
      const attempt = () => {
        if (closed) return reject(new Error('本机桥已关闭'));
        const candidate = net.connect({ host, port });
        let settled = false;
        const fail = error => {
          if (settled) return;
          settled = true;
          candidate.destroy();
          if (Date.now() >= deadline) reject(error);
          else setTimeout(attempt, 250);
        };
        candidate.once('error', fail);
        candidate.setNoDelay?.(true);
        candidate.once('connect', () => {
          if (settled) return;
          settled = true;
          candidate.off('error', fail);
          socket = candidate;
          buffer = '';
          socket.on('data', onData);
          socket.on('error', error => { log(`本机桥连接错误：${error?.message ?? error}`); if (socket === candidate) reset(); });
          socket.on('close', () => { if (socket === candidate) reset(); });
          socket.write(`${JSON.stringify({ token })}\n`);
          resolve();
        });
      };
      attempt();
    }).finally(() => { connecting = undefined; });
    return connecting;
  };

  const bridge = async (operation, payload = {}, signal) => {
    if (signal?.aborted) throw new Error('操作已取消');
    await connect();
    if (signal?.aborted) throw new Error('操作已取消');
    const id = String(++nextId);
    const message = { id, operation, ...payload };
    return await new Promise((resolve, reject) => {
      let timer;
      const cleanup = () => {
        if (timer) clearTimeout(timer);
        signal?.removeEventListener('abort', onAbort);
      };
      const onAbort = () => {
        pending.delete(id);
        abandoned.add(id);
        cleanup();
        reject(new Error('操作已取消'));
      };
      signal?.addEventListener('abort', onAbort, { once: true });
      timer = setTimeout(() => {
        pending.delete(id);
        abandoned.add(id);
        cleanup();
        reject(new Error(`本机桥请求超时：${operation}`));
      }, timeoutFor(operation));
      pending.set(id, { resolve, reject, cleanup });
      try {
        socket.write(`${JSON.stringify(message)}\n`);
      } catch (error) {
        pending.delete(id);
        cleanup();
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  };

  bridge.connect = () => connect();
  bridge.close = () => {
    closed = true;
    rejectAll(new Error('本机桥已关闭'));
    if (socket) socket.destroy();
    socket = undefined;
    connecting = undefined;
  };
  return bridge;
}

/**
 * Line-protocol bridge adapter for the retired WebView launcher: the caller
 * owns the QINGYE frame writer and the incoming `result` frames; this adapter
 * keeps the id matching and abort semantics identical to the TCP client.
 * @param {{emit: (message: object) => void}} options
 * @returns {(operation: string, payload: object, signal?: AbortSignal) => Promise<unknown>}
 */
export function createLineBridge({ emit }) {
  let nextId = 0;
  const pending = new Map();
  const bridge = (operation, payload = {}, signal) => new Promise((resolve, reject) => {
    if (signal?.aborted) return reject(new Error('操作已取消'));
    const id = String(++nextId);
    const cleanup = () => signal?.removeEventListener('abort', onAbort);
    const onAbort = () => {
      pending.delete(id);
      cleanup();
      try { emit({ kind: 'cancel', id }); } catch { /* the transport is already gone */ }
      reject(new Error('操作已取消'));
    };
    signal?.addEventListener('abort', onAbort, { once: true });
    pending.set(id, { resolve, reject, cleanup });
    try { emit({ kind: 'native', id, operation, ...payload }); } catch (error) { pending.delete(id); cleanup(); reject(error); }
  });
  bridge.settle = message => {
    const request = pending.get(message.id);
    if (!request) return false;
    pending.delete(message.id);
    request.cleanup();
    if (message.error) request.reject(new Error(message.error));
    else request.resolve(message.value);
    return true;
  };
  bridge.close = () => {
    for (const request of pending.values()) { request.cleanup(); request.reject(new Error('本机桥已关闭')); }
    pending.clear();
  };
  return bridge;
}
