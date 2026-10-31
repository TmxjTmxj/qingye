import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer as createHttpServer } from 'node:http';
import net from 'node:net';
import { mkdir, mkdtemp, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createComposition, launchEnvironment, parseControlLine } from '../src/Harness/composition.mjs';
import {
  DISABLED_ROWS, NATIVE_TOOL_NAMES, PERSONA, SDK_PROFILE,
  createSdkComposition, sdkAppHome, sdkLaunchEnvironment, sdkModel,
} from '../src/Harness/sdk-composition.mjs';
import { createLineBridge, createTcpBridge, installNativeBridge } from '../src/Harness/native-bridge.mjs';
import { apply } from '../src/Harness/native-tools.mjs';
import { Context } from '../src/Harness/node_modules/@deepseek-ai/cordis/lib/index.js';
import ToolRuntime from '../src/Harness/node_modules/@deepseek-ai/dsh-tools/lib/index.js';
import SystemPrompt from '../src/Harness/node_modules/@deepseek-ai/dsh-system-prompt/lib/index.js';
import { createScope } from '../src/Harness/node_modules/@deepseek-ai/dsh-scope/lib/index.js';
import { MemorySessionPersistence, MemoryStorageBackend } from '../src/Harness/memory-storage.mjs';
import { NativeSdkServer } from '../src/Harness/sdk-native-server.mjs';
import { includeRuntimeFile } from '../scripts/copy-harness-runtime.mjs';

const HARNESS_DIR = fileURLToPath(new URL('../src/Harness', import.meta.url));

test('native SDK runtime omits the retired web UI and keeps SDK code and licenses', () => {
  assert.equal(includeRuntimeFile('@deepseek-ai/dsh-web/lib/index.js'), false);
  assert.equal(includeRuntimeFile('@deepseek-ai/dsh-ui-chat/lib/index.js'), false);
  assert.equal(includeRuntimeFile('@deepseek-ai/dsh-sdk-minimal/cordis.patch.yml'), true);
  assert.equal(includeRuntimeFile('@deepseek-ai/dsh-sdk-jsonrpc-server/LICENSE'), true);
  assert.equal(includeRuntimeFile('@deepseek-ai/dsh-sdk-protocol/lib/index.js'), true);
  assert.equal(includeRuntimeFile('@deepseek-ai/dsh-sdk-protocol/lib/types/index.d.ts'), false);
});
const METRICS_DIR = new URL('../artifacts/metrics/', import.meta.url);
const NATIVE_BRIDGE = Symbol.for('qingye.nativeBridge');
const SDK_ENV = {
  QINGYE_MODEL_BASE_URL: 'http://127.0.0.1:9/v1',
  QINGYE_MODEL_NAME: 'qingye-test',
  QINGYE_MODEL_KEY: 'test-secret-key',
};

function listen(server) {
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => resolve(server.address().port));
  });
}

function lineFrames(stream, onFrame) {
  let buffer = '';
  stream.on('data', chunk => {
    buffer += chunk.toString('utf8');
    let end;
    while ((end = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, end).trim();
      buffer = buffer.slice(end + 1);
      if (!line) continue;
      let frame;
      try { frame = JSON.parse(line); } catch { continue; }
      onFrame(frame);
    }
  });
}

// ── Web (0.9) composition evidence stays green although that scheme is retired.

test('composition pins the real Harness, loopback listener, native tools and disabled telemetry', async () => {
  const manifest = JSON.parse(await readFile(new URL('../src/Harness/package.json', import.meta.url)));
  assert.equal(manifest.dependencies['@deepseek-ai/dsh'], '0.2.1-rc.5');
  const config = createComposition({ runtime: 'D:/轻页/Harness', model: { name: 'test-model', baseURL: 'https://example.test/v1' } });
  const row = id => config.find(entry => entry.id === id);
  assert.equal(row('webserver').config.host, '127.0.0.1');
  assert.equal(row('webserver').config.port, 0);
  assert.equal(row('agent-preset-registry').config.default, 'qingye');
  assert.equal(row('preset-standard').disabled, true);
  assert.equal(row('session-telemetry-otel').disabled, true);
  assert.equal(row('spill-local').disabled, true);
  assert.equal(row('spill-policy').disabled, true);
  assert.equal(row('workspace-files').disabled, true);
  assert.equal(row('plugin-manager').disabled, true);
  const preset = config.find(entry => entry.insert)?.insert.find(entry => entry.id === 'preset-qingye');
  assert.equal(preset.config.id, 'qingye');
  assert(preset.config.plugins.some(entry => entry.id === 'qingye-native-tools'));
  const compaction = preset.config.plugins.find(entry => entry.id === 'compaction');
  assert.equal(compaction?.isolate.compaction, true);
  assert(compaction.config.some(entry => entry.id === 'command-compact'));
  assert.equal(preset.config.plugins.find(entry => entry.id === 'tool-todo').config?.allowParallelInProgress, false);
  assert(!JSON.stringify(config).includes('sk-test-secret'));
});

test('launch environment is isolated from user Harness and ambient network/debug settings', () => {
  const env = launchEnvironment({ DSH_HOME: 'elsewhere', DSH_PERMISSION_MODE: 'danger-full-access', NODE_OPTIONS: '--inspect', HTTPS_PROXY: 'http://proxy', PATH: 'keep', OPENAI_API_KEY: 'other-secret' }, 'D:/轻页/harness-data');
  assert.equal(env.DSH_HOME, path.resolve('D:/轻页/harness-data'));
  assert.equal(env.DSH_TELEMETRY_DISABLED, '1');
  assert.equal(env.DSH_PERMISSION_MODE, 'workspace-write');
  assert.equal(env.NODE_OPTIONS, undefined);
  assert.equal(env.OPENAI_API_KEY, undefined);
  assert.equal(env.PATH, 'keep');
});

test('control protocol never interprets ordinary Harness output as native commands', () => {
  assert.equal(parseControlLine('dsh web: http://127.0.0.1:3080/?token=private'), null);
  assert.equal(parseControlLine('QINGYE {"kind":"ready","url":"https://evil.test"}'), null);
  assert.equal(parseControlLine('QINGYE {"kind":"ready","url":"http://127.0.0.1:43121/?token=test"}').kind, 'ready');
  assert.equal(parseControlLine('QINGYE invalid'), null);
});

// ── SDK (0.10) composition: sdk-minimal, six native tools, no official surface.

test('sdk composition targets sdk-minimal with only the bridge-backed native tool surface', () => {
  assert.equal(SDK_PROFILE, 'sdk-minimal');
  assert.deepEqual([...NATIVE_TOOL_NAMES].sort(), ['create_file', 'edit_file', 'list_files', 'read_file', 'search_files', 'write_file']);
  const appHome = path.resolve('D:/轻页/agent-data');
  const config = createSdkComposition({ runtime: HARNESS_DIR, env: SDK_ENV, appHome });
  const row = id => config.find(entry => entry.id === id);

  for (const id of ['tool-fs', 'tool-working-directory', 'pty', 'terminal-bash', 'terminal-pwsh', 'persistent-bash', 'persistent-pwsh', 'compaction-basic', 'image-offload', 'mcp-resources', 'jobs', 'llm-deepseek']) {
    assert(row(id)?.disabled, `${id} must be disabled`);
    assert(DISABLED_ROWS.includes(id), `${id} must be listed`);
  }
  assert.equal(row('system-prompt').config.includeHarnessIdentity, false);
  assert.equal(row('system-prompt').config.includeRuntimeContext, false);
  assert.equal(row('system-prompt').config.personaPrefix, PERSONA);
  assert.match(row('system-prompt').config.personaPrefix, /轻页/);
  assert.match(row('system-prompt').config.personaPrefix, /[\u4e00-\u9fa5]/);
  assert.equal(row('sessions').config.root, path.join(appHome, 'sessions'));
  assert.equal(row('sessions').config.compression, 'none');

  const insert = config.find(entry => entry.insert).insert;
  const byId = id => insert.find(entry => entry.id === id);
  const provider = byId('llm-pi-ai').config.providers['qingye-model'];
  assert.equal(new URL(byId('llm-pi-ai').name).protocol, 'file:', 'adapter must resolve from the shipped SDK, not an optional web profile');
  assert.equal(provider.api, 'openai-completions');
  assert.equal(provider.apiKeyEnv, 'QINGYE_MODEL_KEY');
  assert.equal(provider.baseURL, SDK_ENV.QINGYE_MODEL_BASE_URL);
  assert.equal(provider.models[0].id, SDK_ENV.QINGYE_MODEL_NAME);
  assert.match(byId('qingye-native-tools').name, /native-tools\.mjs$/);
  // Write approval goes through the loopback bridge, so no SDK approval shim is mounted.
  assert(!insert.some(entry => entry.id === 'qingye-approval'));
  assert(!insert.some(entry => entry.id === 'qingye-approval-bridge'));
  assert(!JSON.stringify(config).includes(SDK_ENV.QINGYE_MODEL_KEY));
  assert.equal(sdkAppHome({ QINGYE_APP_HOME: appHome }), appHome);
  assert.deepEqual(sdkModel(SDK_ENV), { baseURL: SDK_ENV.QINGYE_MODEL_BASE_URL, name: SDK_ENV.QINGYE_MODEL_NAME });
  assert.throws(() => sdkModel({ QINGYE_MODEL_KEY: 'x' }), /QINGYE_MODEL_BASE_URL/);
});

test('production sdk composition uses ephemeral sessions without a JSONL writer', () => {
  const config = createSdkComposition({ runtime: HARNESS_DIR, env: SDK_ENV });
  assert.equal(config.find(row => row.id === 'sessions')?.disabled, true);
  const inserted = config.flatMap(row => row.insert ?? []);
  assert(inserted.some(row => /session-memory\.mjs$/.test(row.name)));
});

test('SDK clear blocks concurrent creation, deduplicates deletion and removes orphan memory logs', async () => {
  let release;
  const idle = new Promise(resolve => { release = resolve; });
  let disposed = 0;
  const orphan = new Set(['orphan']);
  const handle = { agent: { cancel() {}, whenIdle: () => idle }, dispose: async () => { disposed++; } };
  const ctx = {
    on: () => () => {},
    agents: { create: async () => handle },
    sessionPersistence: { list: async () => [...orphan].map(id => ({ header: { id } })), remove: async id => { orphan.delete(id); } },
  };
  const server = new NativeSdkServer(ctx, { notify() {} });
  server.sessions.set('active', { handle });
  const clearing = server.handleRequest('qingye/sessions/clear', {});
  try {
    await assert.rejects(server.getOrCreateSession('racing'), /清除/);
    const deleting = server.deleteSession('active');
    release();
    await Promise.all([clearing, deleting]);
    assert.equal(disposed, 1, 'same handle was disposed twice');
    assert.equal(server.sessions.size, 0);
    assert.equal(orphan.size, 0, 'failed creation left its log after clear');
  } finally { release(); await clearing; }
});

test('sdk launch environment drops ambient Harness state and pins the editor home', () => {
  const env = sdkLaunchEnvironment({
    DSH_HOME: 'elsewhere', DSH_PERMISSION_MODE: 'danger-full-access', DSH_PRIMARY_RUNTIME: 'x',
    NODE_OPTIONS: '--inspect', NODE_PATH: 'x', DEEPSEEK_API_KEY: 'other', HTTPS_PROXY: 'http://proxy',
    PATH: 'keep', QINGYE_MODEL_KEY: 'model-key', QINGYE_BRIDGE_TOKEN: 'bridge-token', QINGYE_BRIDGE_PORT: '1234',
  }, 'D:/轻页/agent-data');
  assert.equal(env.DSH_HOME, path.resolve('D:/轻页/agent-data'));
  assert.equal(env.DSH_TELEMETRY_DISABLED, '1');
  assert.equal(env.DSH_PERMISSION_MODE, 'workspace-write');
  assert.equal(env.DSH_PRIMARY_RUNTIME, undefined);
  assert.equal(env.NODE_OPTIONS, undefined);
  assert.equal(env.NODE_PATH, undefined);
  assert.equal(env.DEEPSEEK_API_KEY, undefined);
  assert.equal(env.PATH, 'keep');
  assert.equal(env.QINGYE_MODEL_KEY, 'model-key');
  assert.equal(env.QINGYE_BRIDGE_PORT, '1234');
});

// ── Local bridge behavior: approval fails closed, never opens silently.

function makeToolContext() {
  const definitions = new Map();
  const ctx = {
    effect: action => action(),
    on: () => () => {},
    tools: {
      register: tool => { definitions.set(tool.name, tool); return () => {}; },
      guard: () => () => {},
    },
    workingDirectory: { get: () => '/test' },
  };
  return { ctx, definitions };
}

async function startBridgeServer(handler) {
  const state = { operations: [], sockets: new Set() };
  const server = net.createServer(socket => {
    state.sockets.add(socket);
    socket.on('error', () => {});
    socket.on('close', () => state.sockets.delete(socket));
    let greeted = false;
    socket.on('data', chunk => {
      let buffer = chunk.toString('utf8');
      let end;
      while ((end = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, end).trim();
        buffer = buffer.slice(end + 1);
        if (!line) continue;
        const message = JSON.parse(line);
        if (!greeted) { greeted = true; state.token = message.token; continue; }
        state.operations.push(message.operation);
        handler({ socket, message, state });
      }
    });
  });
  const port = await listen(server);
  return { port, state, close: () => { for (const socket of state.sockets) socket.destroy(); server.close(); } };
}

for (const scenario of ['rejected', 'disconnect', 'timeout']) {
  test(`native write approval fails closed when the bridge answers ${scenario}`, async () => {
    const { port, state, close } = await startBridgeServer(({ socket, message, state }) => {
      if (message.operation === 'pin') { socket.write(`${JSON.stringify({ id: message.id, value: { ok: true } })}\n`); return; }
      if (message.operation === 'tool') { socket.write(`${JSON.stringify({ id: message.id, value: { id: `proposal-${state.operations.length}`, diff: 'old -> new', requiresApproval: true } })}\n`); return; }
      if (message.operation === 'approval') {
        if (scenario === 'rejected') socket.write(`${JSON.stringify({ id: message.id, value: { allowed: false } })}\n`);
        else if (scenario === 'disconnect') socket.destroy();
        return; // timeout: never answer
      }
      if (message.operation === 'discard') { socket.write(`${JSON.stringify({ id: message.id, value: 'discarded' })}\n`); return; }
      socket.write(`${JSON.stringify({ id: message.id, value: `applied-${message.operation}` })}\n`);
    });
    const bridge = createTcpBridge({ port, token: 'bridge-token', connectTimeoutMs: 3000, timeouts: { approval: 200, discard: 200 } });
    installNativeBridge(bridge);
    const { ctx, definitions } = makeToolContext();
    apply(ctx);
    const exec = { agent: { session: { id: 'session-approval' } }, callId: 'call-approval', signal: new AbortController().signal };
    try {
      const result = await definitions.get('write_file').execute({ path: 'note.md', content: 'new' }, exec);
      assert.equal(result, '用户未批准，文件未修改。');
      assert(!state.operations.includes('apply'), `${scenario}: apply must not run without approval`);
      assert.equal(state.operations.filter(operation => operation === 'approval').length, 1);
      assert.equal(state.operations.at(-1), 'discard');
    } finally {
      bridge.close();
      close();
      delete globalThis[NATIVE_BRIDGE];
    }
  });
}

// ── Native tools over the bridge (legacy line and TCP transports alike).

test('native write tools wait for bridge approval before applying exactly once', async () => {
  const operations = [];
  let allowed = false;
  globalThis[NATIVE_BRIDGE] = async (operation, payload) => {
    operations.push({ operation, payload });
    if (operation === 'tool') return { id: 'proposal-one', diff: 'old -> new', requiresApproval: true };
    if (operation === 'approval') return { allowed };
    return 'written';
  };
  const { ctx, definitions } = makeToolContext();
  apply(ctx);
  const exec = { agent: { session: { id: 'session-one' } }, callId: 'call-one', signal: new AbortController().signal };
  try {
    const write = definitions.get('write_file');
    assert.equal(await write.execute({ path: 'note.md', content: 'new' }, exec), '用户未批准，文件未修改。');
    assert(!operations.some(entry => entry.operation === 'apply'), 'write_file applied before approval');
    assert.equal(operations.at(-1).operation, 'discard');
    allowed = true;
    assert.equal(await write.execute({ path: 'note.md', content: 'new' }, exec), 'written');
    assert.equal(operations.filter(entry => entry.operation === 'apply').length, 1);
    assert.equal(operations.at(-1).operation, 'discard');
    const approval = operations.find(entry => entry.operation === 'approval');
    assert.equal(approval.payload.name, 'write_file');
    assert.equal(approval.payload.path, 'note.md');
    assert.equal(approval.payload.diff, 'old -> new');
    assert.equal(approval.payload.proposalId, 'proposal-one');
  } finally {
    delete globalThis[NATIVE_BRIDGE];
  }
});

test('native edit_file tool registers, stays behind bridge approval and inside the guard', async () => {
  const operations = [];
  let allowed = false;
  globalThis[NATIVE_BRIDGE] = async (operation, payload) => {
    operations.push({ operation, payload });
    if (operation === 'tool') return { id: 'proposal-edit', diff: '-old\n+new', requiresApproval: true };
    if (operation === 'approval') return { allowed };
    return 'written';
  };
  const { ctx, definitions } = makeToolContext();
  // The guard still rejects official tools no matter what the runtime composes.
  ctx.tools.guard = rule => {
    assert.equal(rule({ name: 'edit_file' }), undefined);
    assert.equal(rule({ name: 'create_file' }), undefined);
    assert(rule({ name: 'pwsh' }));
    return () => {};
  };
  apply(ctx);
  for (const name of ['list_files', 'read_file', 'search_files', 'write_file', 'edit_file', 'create_file']) {
    assert(definitions.has(name), `${name} was not registered`);
    assert.match(definitions.get(name).description, /[\u4e00-\u9fa5]/, `${name} description is not Chinese`);
  }
  const edit = definitions.get('edit_file');
  assert.deepEqual(edit.parameters.required, ['path', 'old_text', 'new_text']);
  assert.equal(edit.parameters.properties.replace_all.type, 'boolean');
  const exec = { agent: { session: { id: 'session-edit' } }, callId: 'call-edit', signal: new AbortController().signal };
  try {
    assert.equal(await edit.execute({ path: 'note.md', old_text: 'old', new_text: 'new' }, exec), '用户未批准，文件未修改。');
    assert(!operations.some(entry => entry.operation === 'apply'), 'edit_file applied before approval');
    assert.equal(operations.at(-1).operation, 'discard');
    allowed = true;
    await edit.execute({ path: 'note.md', old_text: 'old', new_text: 'new' }, exec);
    const applied = operations.filter(entry => entry.operation === 'apply');
    assert.equal(applied.length, 1, 'edit_file did not apply exactly once');
    assert.equal(applied[0].payload.proposal, 'proposal-edit');
  } finally {
    delete globalThis[NATIVE_BRIDGE];
  }
});

test('targeted preset mounts against the real published scoped tool registry', async () => {
  const ctx = new Context();
  globalThis[NATIVE_BRIDGE] = async () => 'test';
  try {
    await ctx.plugin(SystemPrompt, {});
    await ctx.plugin(ToolRuntime);
    let scope;
    await ctx.plugin(Object.assign(inner => { scope = createScope(inner, {}); }, { inject: ['tools', 'systemPrompt'] }));
    assert.doesNotThrow(() => apply(scope.ctx));
    await scope.dispose();
  } finally {
    delete globalThis[NATIVE_BRIDGE];
    await ctx.fiber.dispose();
  }
});

test('conversation storage is memory-only and a new runtime starts empty', async () => {
  const ctx = new Context();
  let memory;
  try {
    await ctx.plugin(inner => { memory = new MemorySessionPersistence(inner); });
    const header = { version: 4, id: 'memory-one', cwd: 'C:/workspace', createdAt: Date.now(), isSeeded: false };
    const writer = await memory.create(header);
    await writer.flush();
    assert.equal((await memory.list()).length, 1);
    await writer.close();
    const reader = await memory.open('memory-one', 'read');
    assert.deepEqual((await reader.read()).events, []);
    await reader.close();
    const other = new Context();
    let empty;
    await other.plugin(inner => { empty = new MemorySessionPersistence(inner); });
    assert.equal((await empty.list()).length, 0);
    await other.fiber.dispose();
    const backend = new MemoryStorageBackend();
    const unit = await backend.kv.open({ name: 'workspace', version: 1, tables: ['items'], hasGlobal: true });
    await unit.putRecord('items', 'one', { title: 'private title' });
    const copy = await unit.loadAll();
    copy.tables.items.one.title = 'changed';
    assert.equal((await unit.loadAll()).tables.items.one.title, 'private title');
    await backend.close();
  } finally { await ctx.fiber.dispose(); }
});

test('memory overflow fails the checkpoint instead of silently freezing history', async () => {
  const ctx = new Context();
  let memory;
  try {
    await ctx.plugin(inner => { memory = new MemorySessionPersistence(inner); });
    const writer = await memory.create({ version: 4, id: 'overflow', cwd: 'C:/workspace', createdAt: Date.now(), isSeeded: false });
    await assert.rejects(writer.append([{ seq: 0, type: 'user/message', data: { content: 'x'.repeat(9 * 1024 * 1024) } }]));
    await assert.rejects(writer.flush(), /8 MiB/);
    await assert.rejects(memory.flush(), /8 MiB/);
    await assert.rejects(ctx.parallel('session/flush', { id: 'overflow' }), error =>
      error instanceof AggregateError && error.errors.length > 0 && error.errors.every(cause => /8 MiB/.test(cause.message)));
    await writer.close();
  } finally { await ctx.fiber.dispose(); }
});

test('SDK memory quota is shared across temporary sessions and deletion releases it', async () => {
  const ctx = new Context();
  let memory;
  try {
    await ctx.plugin(inner => { memory = new MemorySessionPersistence(inner, { maximumTotalBytes: 4096 }); });
    const first = await memory.create({ version: 4, id: 'budget-one', cwd: 'C:/workspace', createdAt: Date.now(), isSeeded: false });
    const second = await memory.create({ version: 4, id: 'budget-two', cwd: 'C:/workspace', createdAt: Date.now(), isSeeded: false });
    const event = { seq: 0, type: 'turn/start', time: Date.now(), data: { turn: 1, padding: 'x'.repeat(2500) } };
    await first.append([event]);
    await assert.rejects(second.append([event]), /总内存/);
    await first.close();
    await second.close();
    await memory.remove('budget-one');
    await memory.remove('budget-two');
    const third = await memory.create({ version: 4, id: 'budget-three', cwd: 'C:/workspace', createdAt: Date.now(), isSeeded: false });
    await third.append([event]);
    await third.close();
    assert.equal((await memory.list()).length, 1);
  } finally { await ctx.fiber.dispose(); }
});

test('legacy QINGYE line bridge keeps the same native tool approval flow', async () => {
  const frames = [];
  const bridge = createLineBridge({ emit: message => frames.push(message) });
  installNativeBridge(bridge);
  const { ctx, definitions } = makeToolContext();
  apply(ctx);
  const exec = { agent: { session: { id: 'session-line' } }, callId: 'call-line', signal: new AbortController().signal };
  const waitForFrame = async operation => {
    for (let attempt = 0; attempt < 200; attempt += 1) {
      const frame = frames.find(entry => entry.operation === operation && entry.id !== undefined);
      if (frame) return frame;
      await new Promise(resolve => setTimeout(resolve, 5));
    }
    throw new Error(`no ${operation} frame`);
  };
  try {
    const pending = definitions.get('write_file').execute({ path: 'note.md', content: 'new' }, exec);
    const toolFrame = await waitForFrame('tool');
    assert.equal(toolFrame.kind, 'native');
    bridge.settle({ id: toolFrame.id, value: { id: 'proposal-line', diff: 'old -> new', requiresApproval: true } });
    const approvalFrame = await waitForFrame('approval');
    assert.equal(approvalFrame.name, 'write_file');
    assert.equal(approvalFrame.path, 'note.md');
    assert.equal(approvalFrame.proposalId, 'proposal-line');
    bridge.settle({ id: approvalFrame.id, value: { allowed: true } });
    const applyFrame = await waitForFrame('apply');
    bridge.settle({ id: applyFrame.id, value: 'written' });
    const discardFrame = await waitForFrame('discard');
    bridge.settle({ id: discardFrame.id, value: 'discarded' });
    assert.equal(await pending, 'written');
  } finally {
    delete globalThis[NATIVE_BRIDGE];
  }
});

// ── Real SDK runtime: cold boot, six tools, bridge approval round trip, persistence, latency.

function sdkMockModel() {
  let calls = 0;
  const http = createHttpServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      calls += 1;
      res.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8', 'Cache-Control': 'no-cache' });
      const base = { id: 'chatcmpl-qingye-test', object: 'chat.completion.chunk', created: Math.floor(Date.now() / 1000), model: 'qingye-test' };
      const send = payload => res.write(`data: ${JSON.stringify(payload)}\n\n`);
      const toolCall = (target, callId) => {
        send({ ...base, choices: [{ index: 0, delta: { role: 'assistant', content: null, tool_calls: [{ index: 0, id: callId, type: 'function', function: { name: 'write_file', arguments: JSON.stringify({ path: target, content: 'hello from sdk' }) } }] }, finish_reason: null }] });
        send({ ...base, choices: [{ index: 0, delta: {}, finish_reason: 'tool_calls' }] });
      };
      if (calls === 1) toolCall('allowed.txt', 'call-allowed');
      else if (calls === 3) toolCall('denied.txt', 'call-denied');
      else {
        send({ ...base, choices: [{ index: 0, delta: { role: 'assistant', content: '已完成。' }, finish_reason: null }] });
        send({ ...base, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] });
      }
      res.write('data: [DONE]\n\n');
      res.end();
    });
  });
  return { http, calls: () => calls };
}

function sdkMockBridge(workspace) {
  const state = { token: undefined, operations: [], approvals: [], applied: 0 };
  const server = net.createServer(socket => {
    socket.on('error', () => {});
    let buffer = '';
    let greeted = false;
    socket.on('data', chunk => {
      buffer += chunk.toString('utf8');
      let end;
      while ((end = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, end).trim();
        buffer = buffer.slice(end + 1);
        if (!line) continue;
        const message = JSON.parse(line);
        if (!greeted) { greeted = true; state.token = message.token; continue; }
        state.operations.push(message.operation);
        if (message.operation === 'pin' || message.operation === 'context') {
          socket.write(`${JSON.stringify({ id: message.id, value: { ok: true } })}\n`);
        } else if (message.operation === 'tool') {
          socket.write(`${JSON.stringify({ id: message.id, value: { id: `proposal-${state.operations.length}`, diff: `--- ${message.args.path}\n+++ ${message.args.path}\n+hello from sdk`, requiresApproval: true } })}\n`);
        } else if (message.operation === 'approval') {
          const allowed = state.approvals.length === 0;
          state.approvals.push({ ...message, allowed });
          socket.write(`${JSON.stringify({ id: message.id, value: { allowed } })}\n`);
        } else if (message.operation === 'apply') {
          state.applied += 1;
          state.operations.push('apply:ok');
          writeFile(path.join(workspace, message.args.path), message.args.content, 'utf8').catch(() => {});
          socket.write(`${JSON.stringify({ id: message.id, value: '已写入' })}\n`);
        } else if (message.operation === 'discard') {
          state.operations.push('discard:ok');
          socket.write(`${JSON.stringify({ id: message.id, value: '已丢弃' })}\n`);
        } else {
          socket.write(`${JSON.stringify({ id: message.id, value: null })}\n`);
        }
      }
    });
  });
  return { server, state };
}

function sdkRpcClient(child) {
  const pending = new Map();
  const notifications = [];
  const serverRequests = [];
  let nextId = 100;
  lineFrames(child.stdout, frame => {
    if (frame.method && frame.id !== undefined) {
      serverRequests.push(frame);
      child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id: frame.id, error: { code: -32601, message: 'unexpected server request' } })}\n`);
      return;
    }
    if (frame.method) { notifications.push(frame); return; }
    const waiter = pending.get(frame.id);
    if (!waiter) return;
    pending.delete(frame.id);
    if (frame.error) waiter.reject(new Error(frame.error.message));
    else waiter.resolve(frame.result);
  });
  const request = (method, params) => new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
  });
  const waitFor = async (predicate, timeoutMs = 30_000) => {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      const found = notifications
        .filter(frame => frame.method === 'session.event')
        .map(frame => frame.params.event)
        .find(predicate);
      if (found) return found;
      await new Promise(resolve => setTimeout(resolve, 20));
    }
    throw new Error('等待 SDK 事件超时');
  };
  return { request, waitFor, notifications, serverRequests };
}

function walkFiles(dir) {
  return readdir(dir, { withFileTypes: true }).then(entries => Promise.all(entries.map(entry => {
    const full = path.join(dir, entry.name);
    return entry.isDirectory() ? walkFiles(full) : [full];
  }))).then(nested => nested.flat()).catch(() => []);
}

test('memory SDK streams before settlement, cancels a turn without restart and forgets sessions', { timeout: 45_000 }, async () => {
  const root = await mkdtemp(path.join(fileURLToPath(new URL('../build/', import.meta.url)), 'sdk-memory-'));
  const workspace = path.join(root, 'workspace');
  const appHome = path.join(root, 'runtime');
  await mkdir(workspace, { recursive: true });
  let calls = 0;
  const model = createHttpServer((req, res) => {
    req.resume();
    req.on('end', () => {
      const call = ++calls;
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      const send = (text, finish = null) => res.write(`data: ${JSON.stringify({
        id: 'memory-stream', object: 'chat.completion.chunk', model: 'qingye-test',
        choices: [{ index: 0, delta: { content: text }, finish_reason: finish }],
      })}\n\n`);
      send(call === 2 ? '等待取消' : '第一段');
      if (call === 2) return;
      const timer = setTimeout(() => { send('第二段'); send('', 'stop'); res.end('data: [DONE]\n\n'); }, 350);
      res.on('close', () => clearTimeout(timer));
    });
  });
  const bridge = sdkMockBridge(workspace);
  let child;
  const waitUntil = async predicate => {
    const deadline = Date.now() + 5000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error('Live SDK condition timed out');
      await new Promise(resolve => setTimeout(resolve, 10));
    }
  };
  try {
    const modelPort = await listen(model);
    const bridgePort = await listen(bridge.server);
    const env = { ...process.env, QINGYE_MODEL_BASE_URL: `http://127.0.0.1:${modelPort}/v1`,
      QINGYE_MODEL_NAME: 'qingye-test', QINGYE_MODEL_KEY: 'memory-test-only', QINGYE_APP_HOME: appHome,
      QINGYE_BRIDGE_PORT: String(bridgePort), QINGYE_BRIDGE_TOKEN: 'test-bridge-token', QINGYE_SESSION_STORAGE: 'memory' };
    child = spawn(process.execPath, [path.join(HARNESS_DIR, 'sdk-launcher.mjs')], { cwd: workspace, stdio: ['pipe', 'pipe', 'pipe'], env });
    const diagnostics = [];
    child.stderr.on('data', chunk => diagnostics.push(chunk.toString()));
    const client = sdkRpcClient(child);
    await client.request('initialize', { provider: 'qingye-model', model: 'qingye-test', cwd: workspace });
    const pid = child.pid;
    const prompt = () => client.request('session/prompt', { sessionId: 'temporary-session', contentBlocks: [{ type: 'text', text: '只回复文本' }] });
    await prompt();
    await waitUntil(() => client.notifications.some(frame => frame.method === 'qingye/assistant.delta'));
    assert(!client.notifications.some(frame => frame.params?.event?.type === 'turn/end'), 'first delta arrived only after the turn ended');
    await client.waitFor(event => event.type === 'turn/end');
    assert.equal(client.notifications.filter(frame => frame.method === 'qingye/assistant.delta').map(frame => frame.params.text).join(''), '第一段第二段');
    client.notifications.length = 0;
    await prompt();
    await waitUntil(() => client.notifications.some(frame => frame.params?.text === '等待取消'));
    const cancelledAt = Date.now();
    const cancelled = await client.request('qingye/session/cancel', { sessionId: 'temporary-session' });
    assert.equal(cancelled.cancelled, true);
    assert.equal(child.pid, pid, 'turn cancellation restarted the runtime');
    assert.equal(child.exitCode, null, 'turn cancellation ended the process');
    client.notifications.length = 0;
    await prompt();
    await client.waitFor(event => event.type === 'turn/end');
    assert.equal(calls, 3);
    assert.equal((await client.request('qingye/session/delete', { sessionId: 'temporary-session' })).deleted, true);
    await assert.rejects(client.request('session/resume', { sessionId: 'temporary-session' }));
    const files = await walkFiles(appHome);
    assert(!files.some(file => /\.(jsonl|zst)$/.test(file)), 'temporary SDK session was archived');
    for (const file of files) assert(!(await readFile(file, 'utf8')).includes('第一段'), 'conversation content spilled to runtime files');
    await mkdir(METRICS_DIR, { recursive: true });
    await writeFile(new URL('optimization-sdk-live.txt', METRICS_DIR), `cancel_and_warm_continue_ms: ${Date.now() - cancelledAt}\nnode_pid_unchanged: true\njsonl_files: 0\n`, 'utf8');
    await client.request('shutdown', {});
  } finally {
    if (child) child.kill();
    bridge.server.close();
    model.closeAllConnections();
    model.close();
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
});

test('sdk launcher boots sdk-minimal, advertises exactly six tools and applies only approved writes', { timeout: 180_000 }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'qingye-sdk-test-'));
  const workspace = path.join(root, 'workspace');
  const appHome = path.join(root, 'agent-data');
  await mkdir(workspace, { recursive: true });

  const model = sdkMockModel();
  const bridge = sdkMockBridge(workspace);
  let child;
  try {
    const modelPort = await listen(model.http);
    const bridgePort = await listen(bridge.server);
    const env = { ...process.env };
    for (const name of Object.keys(env)) if (/^(DSH_|NODE_OPTIONS$|NODE_PATH$)/i.test(name)) delete env[name];
    Object.assign(env, {
      QINGYE_MODEL_BASE_URL: `http://127.0.0.1:${modelPort}/v1`,
      QINGYE_MODEL_NAME: 'qingye-test',
      QINGYE_MODEL_KEY: 'test-secret-key',
      QINGYE_APP_HOME: appHome,
      QINGYE_BRIDGE_PORT: String(bridgePort),
      QINGYE_BRIDGE_TOKEN: 'test-bridge-token',
      // This existing scenario verifies legacy JSONL compatibility explicitly;
      // the production memory-only scenario is covered separately.
      QINGYE_SESSION_STORAGE: 'jsonl',
    });
    const started = Date.now();
    child = spawn(process.execPath, [path.join(HARNESS_DIR, 'sdk-launcher.mjs')], { cwd: workspace, stdio: ['pipe', 'pipe', 'pipe'], env });
    const stderr = [];
    child.stderr.on('data', chunk => stderr.push(chunk.toString('utf8')));
    const client = sdkRpcClient(child);

    const initialize = await client.request('initialize', {
      provider: 'qingye-model', model: 'qingye-test', cwd: workspace,
      approvalRequests: true, capabilities: { userQuestions: false },
    });
    const coldMs = Date.now() - started;
    assert.equal(initialize.serverInfo.name, 'deepseek-harness-sdk-runtime');
    assert(bridge.state.token === 'test-bridge-token', 'bridge token handshake missing');

    const promptStarted = Date.now();
    await client.request('session/prompt', { sessionId: 'sdk-session-1', contentBlocks: [{ type: 'text', text: '请写入 allowed.txt' }] });
    const header = await client.waitFor(event => event.type === 'request/header');
    const firstEventMs = Date.now() - started;
    const tools = header.data.header.tools.map(tool => tool.name).sort();
    assert.deepEqual(tools, [...NATIVE_TOOL_NAMES].sort(), 'request/header must advertise only the six native tools');

    const firstTurn = await client.waitFor(event => event.type === 'turn/end' && event.data.turn === 1);
    const firstPromptMs = Date.now() - promptStarted;
    assert.equal(firstTurn.data.reason.kind, 'completed');
    assert.equal(await stat(path.join(workspace, 'allowed.txt')).then(() => true, () => false), true);
    assert.equal(bridge.state.applied, 1);
    const firstApproval = bridge.state.operations.indexOf('approval');
    const firstApply = bridge.state.operations.indexOf('apply');
    assert(firstApproval >= 0 && firstApply > firstApproval, 'apply must follow the bridge approval');
    assert(bridge.state.approvals[0].name === 'write_file');
    assert.match(bridge.state.approvals[0].diff, /allowed\.txt/);
    assert(bridge.state.approvals[0].proposalId);

    const warmStarted = Date.now();
    await client.request('session/prompt', { sessionId: 'sdk-session-1', contentBlocks: [{ type: 'text', text: '再写 denied.txt' }] });
    const secondTurn = await client.waitFor(event => event.type === 'turn/end' && event.data.turn === 2);
    const warmPromptMs = Date.now() - warmStarted;
    assert.equal(secondTurn.data.reason.kind, 'completed');
    assert.equal(await stat(path.join(workspace, 'denied.txt')).then(() => true, () => false), false, 'rejected write must not touch disk');
    assert.equal(bridge.state.applied, 1, 'rejected write must not apply');
    assert.equal(bridge.state.approvals[1].allowed, false);

    const toolResults = client.notifications
      .filter(frame => frame.method === 'session.event' && frame.params.event.type === 'tool/result')
      .map(frame => frame.params.event.data.message.content?.[0]?.text);
    assert(toolResults.includes('已写入'));
    assert(toolResults.includes('用户未批准，文件未修改。'));

    // Persistence flushes are batched, so a fast turn can settle before the
    // JSONL file materializes; allow a bounded wait instead of racing it.
    let sessionFiles = [];
    const persistDeadline = Date.now() + 5000;
    do {
      sessionFiles = await walkFiles(path.join(appHome, 'sessions'));
      if (sessionFiles.some(file => file.endsWith('.jsonl'))) break;
      await new Promise(resolve => setTimeout(resolve, 50));
    } while (Date.now() < persistDeadline);
    assert(sessionFiles.some(file => file.endsWith('.jsonl')), 'session JSONL must be persisted');
    const sessionFile = sessionFiles.find(file => file.endsWith('.jsonl'));
    let sessionText = '';
    const headerDeadline = Date.now() + 5000;
    do {
      sessionText = await readFile(sessionFile, 'utf8');
      if (sessionText.includes('"type":"request/header"')) break;
      await new Promise(resolve => setTimeout(resolve, 50));
    } while (Date.now() < headerDeadline);
    assert.match(sessionText, /"type":"session"/);
    assert.match(sessionText, /"type":"request\/header"/);
    assert.equal(client.serverRequests.length, 0, 'stdout must carry only JSON-RPC responses/notifications');
    const stderrText = stderr.join('');
    assert(!stderrText.includes('test-secret-key'), 'stderr must never expose the model key');
    assert(!stderrText.includes('test-bridge-token'), 'stderr must never expose the bridge token');

    await mkdir(METRICS_DIR, { recursive: true });
    await writeFile(new URL('sdk-node-boot.txt', METRICS_DIR), [
      `source: tests/harness.test.mjs (sdk launcher e2e)`,
      `profile: ${SDK_PROFILE}`,
      `cold_initialize_ms: ${coldMs}`,
      `first_event_ms: ${firstEventMs}`,
      `first_prompt_ms: ${firstPromptMs}`,
      `warm_second_prompt_ms: ${warmPromptMs}`,
      `tools: ${tools.join(',')}`,
      `bridge_ops: ${bridge.state.operations.join(',')}`,
      `session_file: ${path.relative(appHome, sessionFile)}`,
      `approval_evidence: allowed -> apply=${bridge.state.applied}; rejected -> file_absent=${!(await stat(path.join(workspace, 'denied.txt')).then(() => true, () => false))}`,
      '',
    ].join('\n'), 'utf8');

    assert(warmPromptMs < coldMs, 'a warm prompt should be faster than the cold boot');
    await client.request('shutdown', {}).catch(() => {});
    await new Promise(resolve => {
      const timer = setTimeout(() => { child.kill(); resolve(); }, 3000);
      child.once('exit', () => { clearTimeout(timer); resolve(); });
    });
    child = undefined;
  } finally {
    if (child) child.kill();
    bridge.server.close();
    model.http.close();
    await new Promise(resolve => setTimeout(resolve, 200));
    await rm(root, { recursive: true, force: true }).catch(() => {});
  }
});
