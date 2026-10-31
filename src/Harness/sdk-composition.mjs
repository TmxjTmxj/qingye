import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const MODULE_RUNTIME = path.dirname(fileURLToPath(import.meta.url));

// The native editor needs the smallest possible SDK runtime: the complete
// standalone `sdk-minimal` tree plus a model adapter, an approval service and
// the six bridge-backed file tools. `sdk` proper boots ~1.4s slower because it
// composes the full dsh-base product foundation.
export const SDK_PROFILE = 'sdk-minimal';

export const NATIVE_TOOL_NAMES = Object.freeze([
  'list_files', 'read_file', 'search_files', 'write_file', 'edit_file', 'create_file',
]);

// Rows of the shipped `sdk-minimal` tree the editor deliberately drops: its
// shell stack (which also waits on terminal services on Windows), the official
// text-file and working-directory tools (the native bridge owns file access),
// automatic compaction (large editor sessions stay inspectable), MCP resources,
// jobs, and unused DeepSeek product adapters.
export const DISABLED_ROWS = Object.freeze([
  'sdk-jsonrpc-server',
  'tool-fs',
  'tool-working-directory',
  'pty',
  'terminal-bash',
  'terminal-pwsh',
  'persistent-bash',
  'persistent-pwsh',
  'compaction-basic',
  'image-offload',
  'mcp-resources',
  'jobs',
  'deepseek-llm-api-extensions',
  'plugin-package-inventory-deepseek',
  'session-log-deepseek',
  'llm-deepseek',
]);

// The editor's assistant speaks Chinese and treats editor authorization as the
// only file scope. It has no shell, network or workspace-tool surface, so the
// persona names neither.
export const PERSONA = [
  '你是「轻页」文本编辑器内置的文件助手。',
  '工作区与当前文件范围由编辑器授权，只能通过提供的文件工具操作。',
  '始终用中文清楚沟通；先读取再修改，编辑已有文件优先用 edit_file。',
  '写文件和新建文件都必须等待编辑器界面的用户批准；用户未批准时不要重试或改写。',
  '不要尝试运行命令、联网或访问授权范围之外的路径；遇到未保存的编辑器缓冲区或文件冲突时保留用户内容。',
].join('');

/** Absolute Harness home the SDK process persists sessions and attachments under. */
export function sdkAppHome(env = process.env, runtime = MODULE_RUNTIME) {
  return path.resolve(env.QINGYE_APP_HOME || path.join(runtime, 'agent-data'));
}

/** Model route from the C# launcher environment; secrets stay in `QINGYE_MODEL_KEY`. */
export function sdkModel(env = process.env) {
  const baseURL = (env.QINGYE_MODEL_BASE_URL || '').trim();
  const name = (env.QINGYE_MODEL_NAME || '').trim();
  if (!baseURL) throw new Error('缺少 QINGYE_MODEL_BASE_URL，无法启动轻页模型。');
  if (!name) throw new Error('缺少 QINGYE_MODEL_NAME，无法启动轻页模型。');
  return { baseURL: baseURL.replace(/\/+$/, ''), name };
}

/**
 * Generate the `--patch` overlay (a JSON top-level array, valid YAML too) for
 * the sdk-minimal profile.
 * @param {{runtime: string, env?: NodeJS.ProcessEnv, appHome?: string}} options runtime is the directory holding native-tools.mjs and node_modules.
 * @returns {unknown[]} loader patch entries.
 */
export function createSdkComposition({ runtime, env = process.env, appHome = sdkAppHome(env, runtime) }) {
  const model = sdkModel(env);
  const rows = DISABLED_ROWS.map(id => ({ id, disabled: true }));
  rows.push(
    {
      id: 'system-prompt',
      config: {
        includeHarnessIdentity: false,
        includeRuntimeContext: false,
        personaPrefix: PERSONA,
      },
    },
    {
      id: 'sessions',
      // Explicit JSONL mode is retained for legacy archive compatibility
      // fixtures. The native editor always pins memory mode at launch.
      disabled: env.QINGYE_SESSION_STORAGE !== 'jsonl',
      config: {
        root: path.join(appHome, 'sessions'),
        // Uncompressed JSONL keeps editor history inspectable and cheap to flush.
        compression: 'none',
      },
    },
    {
      insert: [
        {
          id: 'qingye-sdk-server', name: pathToFileURL(path.resolve(runtime, 'sdk-native-server.mjs')).href,
          inject: ['sdkAppStartup', 'loader'],
        },
        ...(env.QINGYE_SESSION_STORAGE === 'jsonl' ? [] : [{
          id: 'qingye-session-memory',
          name: pathToFileURL(path.resolve(runtime, 'session-memory.mjs')).href,
        }]),
        {
          id: 'llm-pi-ai',
          // The pruned runtime keeps this model adapter as a direct file URL:
          // the pinned CLI's dependency graph (which normally exposes adapters
          // through dsh-base) is not copied, so an installation-scope package
          // name would not resolve from the editor profile directory.
          name: pathToFileURL(path.resolve(runtime, 'node_modules/@deepseek-ai/dsh-llm-pi-ai/lib/index.js')).href,
          config: {
            providers: {
              'qingye-model': {
                displayName: '轻页模型',
                apiKeyEnv: 'QINGYE_MODEL_KEY',
                api: 'openai-completions',
                baseURL: model.baseURL,
                defaultContextWindow: 131072,
                defaultMaxTokens: 8192,
                retryPolicy: { mode: 'normal', maxRetries: 2 },
                models: [{ id: model.name, name: model.name }],
              },
            },
          },
        },
        // Write approval travels the loopback TCP bridge (`approval` operation),
        // so the tree needs no ctx.approval service and stdout stays pure JSON-RPC.
        { id: 'qingye-native-tools', name: pathToFileURL(path.resolve(runtime, 'native-tools.mjs')).href },
      ],
    },
  );
  return rows;
}

/**
 * Environment for the SDK process: drop ambient Harness/debug/credential state
 * inherited from the caller, then pin the editor's own home and policy.
 * @param {NodeJS.ProcessEnv} inherited caller environment.
 * @param {string} appHome stable application data directory.
 * @returns {Record<string, string>} the child environment.
 */
export function sdkLaunchEnvironment(inherited, appHome) {
  const env = Object.fromEntries(Object.entries(inherited).filter(([name]) =>
    !/^(DSH_|NODE_OPTIONS$|NODE_PATH$|.*API_KEY$|.*ACCESS_TOKEN$|.*AUTH_TOKEN$)/i.test(name)));
  env.DSH_HOME = path.resolve(appHome);
  env.DSH_TELEMETRY_DISABLED = '1';
  env.DSH_PERMISSION_MODE = 'workspace-write';
  return env;
}
