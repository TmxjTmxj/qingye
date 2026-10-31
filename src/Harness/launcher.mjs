import { createInterface } from 'node:readline';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createComposition, launchEnvironment } from './composition.mjs';
import { createLineBridge, installNativeBridge } from './native-bridge.mjs';

const runtime = path.dirname(fileURLToPath(import.meta.url));
const output = process.stdout.write.bind(process.stdout);
const emit = message => output(`QINGYE ${JSON.stringify(message)}\n`);
let initialized = false;
let key = '';
let logBuffer = '';
globalThis[Symbol.for('qingye.contextListeners')] = new Set();

// The bootstrap token goes only to the owning WebView, never to a log file.
process.stdout.write = (chunk, encoding, callback) => {
  logBuffer += Buffer.isBuffer(chunk) ? chunk.toString('utf8') : chunk;
  let end;
  while ((end = logBuffer.indexOf('\n')) >= 0) {
    const line = logBuffer.slice(0, end).trim();
    logBuffer = logBuffer.slice(end + 1);
    const match = /dsh web:\s*(http:\/\/127\.0\.0\.1:\d+\/\S*)/.exec(line);
    if (match) emit({ kind: 'ready', url: match[1] });
    else if (line) emit({ kind: 'diagnostic', message: line.replaceAll(key || '\u0000', '***').slice(0, 1200) });
  }
  if (typeof encoding === 'function') encoding();
  if (callback) callback();
  return true;
};

const nativeBridge = createLineBridge({ emit });
installNativeBridge(nativeBridge);

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on('line', async line => {
  try {
    if (line.length > 4 * 1024 * 1024) throw new Error('原生控制消息超限');
    const message = JSON.parse(line);
    if (message.kind === 'initialize' && !initialized) {
      initialized = true;
      key = message.model.key;
      const env = launchEnvironment(process.env, message.home);
      for (const name of Object.keys(process.env)) delete process.env[name];
      Object.assign(process.env, env, { QINGYE_MODEL_KEY: key });
      await mkdir(message.home, { recursive: true });
      const overlay = path.join(message.home, 'qingye.patch.json');
      await writeFile(overlay, JSON.stringify(createComposition({ runtime, model: message.model, home: message.home }), null, 2));
      // Always use the supported profile launcher from the pinned official package.
      const cli = path.join(runtime, 'node_modules/@deepseek-ai/dsh/lib/bin.js');
      process.argv = [process.execPath, cli, '--profile', 'web', '--patch', overlay, '--no-open'];
      const { runCli } = await import(pathToFileURL(cli).href);
      await runCli();
    } else if (message.kind === 'result') {
      nativeBridge.settle(message);
    } else if (message.kind === 'shutdown') {
      process.emit('SIGTERM', 'SIGTERM');
    } else if (message.kind === 'context-changed') {
      for (const listener of globalThis[Symbol.for('qingye.contextListeners')]) await listener(message.context);
    }
  } catch (error) {
    emit({ kind: 'error', message: String(error.message).replaceAll(key || '\u0000', '***').slice(0, 1200) });
  }
});
input.on('close', () => process.emit('SIGTERM', 'SIGTERM'));
