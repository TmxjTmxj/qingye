#!/usr/bin/env node
// Launches the pinned official DeepSeek Harness `sdk-minimal` profile inside
// this process. stdin/stdout belong to the newline-delimited JSON-RPC SDK
// protocol; the editor's native bridge is a separate loopback TCP connection.
// Every diagnostic goes to stderr with the model key and bridge token redacted.
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { SDK_PROFILE, createSdkComposition, sdkAppHome, sdkLaunchEnvironment, sdkModel } from './sdk-composition.mjs';
import { createTcpBridge, installNativeBridge } from './native-bridge.mjs';

const runtime = path.dirname(fileURLToPath(import.meta.url));
// Snapshot before replacing process.env: filtering mutates the inherited object.
const inherited = { ...process.env };
const secrets = [inherited.QINGYE_MODEL_KEY, inherited.QINGYE_BRIDGE_TOKEN]
  .filter(value => typeof value === 'string' && value.length >= 4);

function sanitize(text) {
  let output = typeof text === 'string' ? text : String(text);
  for (const secret of secrets) output = output.split(secret).join('***');
  return output;
}

const writeStderr = process.stderr.write.bind(process.stderr);
process.stderr.write = (chunk, encoding, callback) => {
  const text = typeof chunk === 'string' ? chunk : Buffer.isBuffer(chunk) ? chunk.toString('utf8') : String(chunk);
  const redacted = sanitize(text);
  if (typeof encoding === 'function') return writeStderr(redacted, encoding);
  return writeStderr(redacted, encoding, callback);
};

function diagnostic(message) {
  try { process.stderr.write(`轻页 SDK：${sanitize(message)}\n`); } catch { /* stderr is gone */ }
}

async function main() {
  const model = sdkModel(inherited);
  if (!inherited.QINGYE_MODEL_KEY) diagnostic('缺少 QINGYE_MODEL_KEY：模型请求可能因缺少凭据失败。');
  const appHome = sdkAppHome(inherited, runtime);
  await mkdir(appHome, { recursive: true });

  const environment = sdkLaunchEnvironment(inherited, appHome);
  const bridgePort = inherited.QINGYE_BRIDGE_PORT;
  const bridgeToken = inherited.QINGYE_BRIDGE_TOKEN ?? '';
  for (const name of Object.keys(process.env)) delete process.env[name];
  Object.assign(process.env, environment, {
    QINGYE_MODEL_BASE_URL: model.baseURL,
    QINGYE_MODEL_NAME: model.name,
    QINGYE_MODEL_KEY: inherited.QINGYE_MODEL_KEY ?? '',
    QINGYE_APP_HOME: appHome,
  });

  const overlayPath = path.join(appHome, 'qingye-sdk.patch.json');
  const overlay = createSdkComposition({ runtime, env: process.env, appHome });
  await writeFile(overlayPath, `${JSON.stringify(overlay, null, 2)}\n`);

  if (bridgePort) {
    const bridge = createTcpBridge({
      port: bridgePort,
      token: bridgeToken,
      log: diagnostic,
      // A human reviews the write proposal in the editor's native diff card;
      // the bridge timeout, not a model turn timeout, bounds that answer.
      timeouts: { approval: 10 * 60_000 },
    });
    installNativeBridge(bridge);
    // Connect eagerly so the editor sees the token handshake before the first
    // model turn; a failed attempt retries on the next request.
    bridge.connect().catch(error => diagnostic(`本机桥尚未就绪：${error?.message ?? error}`));
  } else {
    installNativeBridge(() => Promise.reject(new Error('未配置 QINGYE_BRIDGE_PORT，文件工具不可用')));
    diagnostic('未配置 QINGYE_BRIDGE_PORT：文件工具不可用。');
  }

  const cli = path.join(runtime, 'node_modules/@deepseek-ai/dsh/lib/bin.js');
  process.argv = [process.execPath, cli, '--profile', SDK_PROFILE, '--patch', overlayPath];
  const { runCli } = await import(pathToFileURL(cli).href);
  await runCli();
}

main().catch(error => {
  diagnostic(`启动失败：${error?.stack ?? error}`);
  process.exitCode = 1;
});

process.on('unhandledRejection', error => {
  diagnostic(`未处理的异步错误：${error?.stack ?? error}`);
  process.exitCode = 1;
});
