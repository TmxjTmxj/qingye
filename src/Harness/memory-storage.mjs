import {
  SessionPersistence, materializeCreateHeader, materializeAppendBatch, assertVersion,
  assertContiguous, SessionAlreadyExistsError, SessionAlreadyOwnedError,
  SessionPersistenceNotFoundError, SessionReadOnlyError, SessionHandleClosedError,
} from '@deepseek-ai/dsh-session-persistence';
import { storageBackendServiceKey } from '@deepseek-ai/dsh-storage';

const copy = value => structuredClone(value);

// Implements the official storage seam without opening or writing a session file.
export class MemorySessionPersistence extends SessionPersistence {
  records = new Map();
  writers = new Map();

  constructor(ctx, { maximumTotalBytes = 32 * 1024 * 1024 } = {}) {
    super(ctx);
    if (!Number.isSafeInteger(maximumTotalBytes) || maximumTotalBytes <= 0) throw new Error('Invalid memory budget');
    this.maximumTotalBytes = maximumTotalBytes;
    ctx.on('session/event', (session, event) => {
      const record = this.records.get(session.id);
      if (record && !record.failure) this.append(record, [event]);
    });
    ctx.on('session/flush', async session => { this.checkpoint(this.records.get(session.id)); });
    ctx.on('session/disposed', session => {
      const writer = this.writers.get(session.id);
      if (writer) void writer.close();
    });
    ctx.effect(() => () => { this.records.clear(); this.writers.clear(); });
  }

  async create(header, options = {}) {
    options.signal?.throwIfAborted();
    if (this.records.has(header.id)) throw new SessionAlreadyExistsError(header.id);
    if (this.records.size >= 32) throw new Error('本次临时会话已达 32 项，请清空对话后继续。');
    const meta = materializeCreateHeader(header);
    assertVersion(meta);
    const record = { header: meta, events: [], bytes: 0, inheritedEventCount: options.inheritedEventCount ?? 0 };
    this.records.set(meta.id, record);
    return this.handle(record, 'write');
  }

  async open(id, access, options = {}) {
    options.signal?.throwIfAborted();
    const record = this.records.get(id);
    if (!record) throw new SessionPersistenceNotFoundError(id);
    return this.handle(record, access);
  }

  append(record, events) {
    this.checkpoint(record);
    try {
      const batch = materializeAppendBatch(events);
      assertContiguous(record.header.id, batch, record.events.length);
      const bytes = Buffer.byteLength(JSON.stringify(batch));
      if (record.bytes + bytes > 8 * 1024 * 1024) throw new Error('本次对话达到 8 MiB 内存预算，请清空对话。');
      const total = [...this.records.values()].reduce((sum, item) => sum + item.bytes, 0);
      if (total + bytes > this.maximumTotalBytes) throw new Error('临时会话总内存达到预算，请清空会话。');
      record.events.push(...batch);
      record.bytes += bytes;
    } catch (error) { record.failure = error; throw error; }
  }

  handle(record, access) {
    const id = record.header.id;
    if (access === 'write' && this.writers.has(id)) throw new SessionAlreadyOwnedError(id);
    let closed = false;
    const check = (operation, options, write = false) => {
      if (closed) throw new SessionHandleClosedError(id, operation);
      options?.signal?.throwIfAborted();
      if (write && access !== 'write') throw new SessionReadOnlyError(id, operation);
    };
    const handle = {
      id, header: copy(record.header), access, inheritedEventCount: record.inheritedEventCount,
      read: async (offset = 0, length = Number.MAX_SAFE_INTEGER, options) => {
        check('read', options);
        if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(length) || length < 0) throw new TypeError('Invalid memory log range');
        return { eventState: 'detached', events: copy(record.events.slice(offset, offset + length)) };
      },
      append: async (events, options) => { check('append', options, true); this.append(record, events); },
      flush: async options => { check('flush', options, true); this.checkpoint(record); },
      close: async () => {
        if (closed) return;
        closed = true;
        if (this.writers.get(id) === handle) this.writers.delete(id);
      },
      [Symbol.asyncDispose]: async () => handle.close(),
    };
    if (access === 'write') this.writers.set(id, handle);
    return handle;
  }

  checkpoint(record) { if (record?.failure) throw record.failure; }
  async flush() { for (const record of this.records.values()) this.checkpoint(record); }
  async stat(id) {
    const record = this.records.get(id);
    return record && { header: copy(record.header), revision: String(record.events.length), formatStatus: 'current', eventCount: record.events.length };
  }
  async list() { return Promise.all([...this.records.keys()].map(id => this.stat(id))); }
  async remove(id) {
    if (this.writers.has(id)) throw new Error('会话仍在使用，不能清除。');
    this.records.delete(id);
  }
}

// Workspace names, titles, projections and tasks also remain in RAM.
export class MemoryStorageBackend {
  units = new Map();
  kv = { open: async descriptor => {
    if (this.units.has(descriptor.name)) throw new Error('Memory unit already open');
    const state = { tables: Object.fromEntries(descriptor.tables.map(name => [name, {}])), global: null };
    this.units.set(descriptor.name, state);
    let closed = false;
    const check = () => { if (closed) throw new Error('Memory unit closed'); };
    return {
      loadAll: async () => { check(); return copy(state); },
      putRecord: async (table, key, value) => { check(); if (!state.tables[table]) throw new Error('Unknown table'); Object.defineProperty(state.tables[table], key, { value: copy(value), enumerable: true, writable: true, configurable: true }); },
      deleteRecord: async (table, key) => { check(); delete state.tables[table][key]; },
      setGlobal: async value => { check(); state.global = copy(value); },
      close: async () => { closed = true; this.units.delete(descriptor.name); },
    };
  } };
  async close() { this.units.clear(); }
}

export const inject = ['storage'];
export function apply(ctx) {
  new MemorySessionPersistence(ctx);
  const backend = new MemoryStorageBackend();
  ctx.effect(() => {
    const unregister = ctx.storage.backend.register('memory', backend);
    return async () => { unregister(); await backend.close(); };
  });
  ctx.provide(storageBackendServiceKey('memory'), backend);
}
