import path from 'node:path';
import { pathToFileURL } from 'node:url';

// A patch over the published web profile, rather than a second agent loop.
export function createComposition({ runtime, model, home = path.join(runtime, 'harness-data') }) {
  // The panel is a plain conversation surface embedded in the native editor:
  // no Harness navigation, workspace manager, settings pages, brand chrome or
  // account/model pickers. Keep only the chat, tool, approval, composer and
  // theme/renderer/session plumbing the conversation itself needs.
  const disabled = [
    'preset-standard', 'preset-minimal', 'preset-cordis', 'preset-ptc',
    'session-telemetry-otel', 'product-analytics', 'ui-message-feedback',
    'plugin-manager', 'tool-plugin-manager', 'ui-plugin-manager', 'entry-customization',
    'ui-settings-agent-preset', 'ui-permission-presets', 'ui-permission', 'workspace-files', 'ui-deliverables',
    'ui-sidebar-files', 'ui-sidebar-terminal', 'terminal-controller', 'ui-sidebar',
    'llm-deepseek', 'llm-deepseek-account', 'session-title-llm', 'hmr',
    'office-to-pdf', 'ui-sidebar-documentpreview', 'ui-settings-models',
    'session-persistence-jsonl', 'storage-json',
    'spill-local', 'spill-policy',
    // Chrome visuals are removed by the AgentSkin stylesheet; these rows would
    // only render navigation/account/model surfaces the panel never shows.
    // (ui-workspace, ui-settings, ui-sidebar-right and ui-trajectory stay
    // mounted because other client plugins inject them.)
    'ui-agent-preset', 'ui-model-selection',
    'ui-deepseek-model-label-1017', 'ui-brand-official', 'open-in-app', 'ui-open-in-app',
    // Settings surfaces and plugin/session tooling.
    'ui-settings-general', 'ui-settings-plugin-inventory', 'ui-settings-plugins',
    'ui-settings-shell', 'ui-settings-agent-loop', 'ui-settings-subagent', 'ui-settings-web-search',
    'ui-settings-account', 'ui-settings-session-log', 'session-log-download', 'message-feedback',
    // Conversation-adjacent features the panel does not expose.
    'ui-jobs', 'ui-schedule', 'ui-goal', 'ui-subagent', 'ui-workflow-run',
    'ui-skill', 'ui-commands', 'ui-plan', 'ui-cordis',
  ];
  const rows = disabled.map(id => ({ id, disabled: true }));
  rows.push(
    { id: 'webserver', config: { host: '127.0.0.1', port: 0 } },
    { id: 'workspace-controller', config: { documentsDirectory: home } },
    { id: 'storage-domain', config: { backend: 'memory' } },
    { id: 'agent-preset-registry', config: { default: 'qingye' } },
    { id: 'locale', config: { preference: 'zh-CN' } },
    { id: 'ui-theme', config: { preference: 'light', textFontFamily: 'Segoe UI, Microsoft YaHei UI', codeFontFamily: 'Cascadia Mono, Consolas, Microsoft YaHei UI' } },
    { id: 'agent-default-model', config: { provider: 'qingye-model', model: model.name } },
    { id: 'llm-pi-ai', config: { providers: {
      'qingye-model': {
        displayName: '轻页模型', apiKeyEnv: 'QINGYE_MODEL_KEY',
        api: 'openai-completions', baseURL: model.baseURL,
        defaultContextWindow: 131072, defaultMaxTokens: 8192,
        retryPolicy: { mode: 'normal', maxRetries: 2 },
        models: [{ id: model.name, name: model.name }],
      },
    } } },
    { insert: [
      { id: 'qingye-memory-storage', name: pathToFileURL(path.resolve(runtime, 'memory-storage.mjs')).href },
      { id: 'qingye-editor-context', name: pathToFileURL(path.resolve(runtime, 'editor-context.mjs')).href }, {
      id: 'preset-qingye', name: '@deepseek-ai/dsh-agent-preset',
      config: {
        id: 'qingye', order: 0,
        plugins: [
          { id: 'persona', name: '@deepseek-ai/dsh-persona', config: {
            prefix: '你是轻页内置的 DeepSeek Harness 文件助手。请用中文清楚沟通，先检查内容，再完成任务。使用 todo_write 跟踪多步工作。文件工具以原生编辑器授权范围为准；不要求用户扩大权限。写文件必须等待界面批准。遇到未保存内容或文件冲突时保留用户工作。',
          } },
          { id: 'qingye-native-tools', name: pathToFileURL(path.resolve(runtime, 'native-tools.mjs')).href },
          { id: 'tool-todo', name: '@deepseek-ai/dsh-tool-todo', config: { allowParallelInProgress: false } },
          { id: 'tool-ask-user', name: '@deepseek-ai/dsh-tool-ask-user' },
          { id: 'compaction', name: 'cordis:group', group: true, isolate: { compaction: true }, config: [
            { id: 'compaction-basic', name: '@deepseek-ai/dsh-compaction-basic' },
            { id: 'command-compact', name: '@deepseek-ai/dsh-command-compact' },
          ] },
        ],
      },
    }] },
  );
  return rows;
}

export function launchEnvironment(inherited, home) {
  const env = Object.fromEntries(Object.entries(inherited).filter(([name]) =>
    !/^(DSH_|NODE_OPTIONS$|NODE_PATH$|.*API_KEY$|.*ACCESS_TOKEN$|.*AUTH_TOKEN$)/i.test(name)));
  env.DSH_HOME = path.resolve(home);
  env.DSH_TELEMETRY_DISABLED = '1';
  env.DSH_PERMISSION_MODE = 'workspace-write';
  return env;
}

export function parseControlLine(line) {
  if (!line.startsWith('QINGYE ')) return null;
  try {
    const value = JSON.parse(line.slice(7));
    if (value.kind === 'ready') {
      const url = new URL(value.url);
      if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port) return null;
    }
    return value;
  } catch { return null; }
}
