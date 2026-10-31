import { HarnessSdkJsonRpcServer } from '@deepseek-ai/dsh-sdk-jsonrpc-server';
import { JsonRpcLineTransport } from '@deepseek-ai/dsh-sdk-protocol';

export const name = 'qingye-sdk-server';
export const inject = ['agents', 'workingDirectory', 'sessionPersistence'];

// Reuse the official SDK server for every standard request. This adapter adds
// live public text, current-turn cancellation and explicit memory disposal.
export class NativeSdkServer extends HarnessSdkJsonRpcServer {
  retiring = new Set();
  deletions = new Map();
  clearing = false;
  clearTask;

  async getOrCreateSession(id) {
    if (this.clearing || this.retiring.has(id)) throw new Error('会话正在清除，请稍后重试。');
    return super.getOrCreateSession(id);
  }

  async cancelSession(id) {
    const pending = this.sessionCreations.get(id);
    if (pending) await pending;
    const record = this.sessions.get(id);
    if (!record) return { cancelled: false };
    record.handle.agent.cancel({ kind: 'user' });
    await record.handle.agent.whenIdle();
    return { cancelled: true };
  }

  deleteSession(id) {
    if (this.deletions.has(id)) return this.deletions.get(id);
    this.retiring.add(id);
    const deleting = this.performDelete(id);
    this.deletions.set(id, deleting);
    deleting.finally(() => { this.deletions.delete(id); this.retiring.delete(id); }).catch(() => {});
    return deleting;
  }

  async performDelete(id) {
      await this.cancelSession(id);
      const record = this.sessions.get(id);
      if (record) {
        await record.handle.dispose();
        this.sessions.delete(id);
      }
      const memory = this.ctx.sessionPersistence;
      if (typeof memory.remove === 'function') await memory.remove(id);
      return { deleted: true };
  }

  async clearSessions() {
    try {
      await Promise.allSettled([...this.sessionCreations.values()]);
      for (const id of [...this.sessions.keys()]) await this.deleteSession(id);
      const memory = this.ctx.sessionPersistence;
      if (typeof memory.remove === 'function')
        for (const record of await memory.list()) await memory.remove(String(record.header.id));
      return { cleared: true };
    } finally { this.clearing = false; this.clearTask = undefined; }
  }

  async handleRequest(method, params) {
    if (method === 'qingye/sessions/clear') {
      if (!this.clearTask) { this.clearing = true; this.clearTask = this.clearSessions(); }
      return this.clearTask;
    }
    if (this.clearing && method.startsWith('session/')) throw new Error('会话正在清除，请稍后重试。');
    if (method === 'qingye/session/cancel' || method === 'qingye/session/delete') {
      const id = params?.sessionId;
      if (typeof id !== 'string' || id.length === 0 || id.length > 256) throw new Error('会话编号无效。');
      return method.endsWith('/cancel') ? this.cancelSession(id) : this.deleteSession(id);
    }
    return super.handleRequest(method, params);
  }
}

export function apply(ctx, config = {}) {
  const transport = new JsonRpcLineTransport(config.input ?? process.stdin, config.output ?? process.stdout);
  const originalNotify = transport.notify.bind(transport);
  const pending = new Map();
  const flush = id => {
    const item = pending.get(id);
    if (!item) return;
    clearTimeout(item.timer);
    pending.delete(id);
    originalNotify('qingye/assistant.delta', { sessionId: id, text: item.parts.join('') });
  };
  // The committed message must follow its final delta on the same pipe.
  transport.notify = (method, params) => {
    if (method === 'session.event' && ['assistant/message', 'turn/end'].includes(params?.event?.type)) flush(params.sessionId);
    return originalNotify(method, params);
  };
  const server = new NativeSdkServer(ctx, transport, { maxTokensAsSuccess: false });
  ctx.effect(() => ctx.on('agent/assistant-stream', ({ agent, frame }) => {
    const id = String(agent.session.id);
    if (!server.sessions.has(id)) return;
    if (frame.type === 'start') {
      flush(id);
      originalNotify('qingye/assistant.start', { sessionId: id, attemptId: frame.attemptId });
    } else if (frame.type === 'chunk' && frame.chunk.type === 'text-delta' && frame.chunk.text) {
      let item = pending.get(id);
      if (!item) {
        item = { parts: [], length: 0, timer: setTimeout(() => flush(id), 40) };
        item.timer.unref?.();
        pending.set(id, item);
      }
      item.parts.push(frame.chunk.text);
      item.length += frame.chunk.text.length;
      if (item.length >= 2048) flush(id);
    } else if (frame.type === 'end') flush(id);
  }));
  transport.onRequest(async (method, params) => {
    if (method === 'initialize') await ctx.get('loader')?.await();
    const result = await server.handleRequest(method, params);
    if (method === 'shutdown') setImmediate(async () => {
      await transport.flush();
      await ctx.root.fiber.dispose();
      (config.exit ?? process.exit)(0);
    });
    return result;
  });
  ctx.effect(() => {
    transport.start();
    return async () => {
      for (const item of pending.values()) clearTimeout(item.timer);
      pending.clear();
      await server.shutdown();
      transport.close();
    };
  }, 'qingye.sdk.serve');
}
