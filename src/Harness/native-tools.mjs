import { defineTool } from '@deepseek-ai/dsh-tools';
import { getNativeBridge } from './native-bridge.mjs';

export const inject = ['tools', 'workingDirectory'];
const tools = [
  ['list_files', '列出授权范围内的文件与目录。path 省略时为工作区根目录；recursive 递归子目录；depth 可为 1-6，指定最多深入的目录层数（1 只列当前目录）。最多返回 200 项。',
    { path: { type: 'string' }, recursive: { type: 'boolean' }, depth: { type: 'integer', description: '递归层数 1-6；不填时沿用 recursive 语义' } }],
  ['read_file', '读取文件或原生编辑器中的最新缓冲区；最多 256 KiB 字符，可用 start_line 和 max_lines 翻页。只读取文本，不要读取密钥或配置文件。',
    { path: { type: 'string', required: true }, start_line: { type: 'integer' }, max_lines: { type: 'integer' } }],
  ['search_files', '在授权文本文件中搜索：默认不区分大小写按纯文本匹配，regex=true 时把 query 当作正则表达式；context 可返回匹配行前后的上下文行。预算：最多检查 200 个文件、返回 50 个结果、单个文件 1 MiB。',
    { query: { type: 'string', required: true }, path: { type: 'string' }, regex: { type: 'boolean', description: '是否按正则表达式匹配，默认 false' }, context: { type: 'integer', description: '匹配行前后各返回的上下文行数 0-10，默认 0' } }],
  ['write_file', '提出完整文件内容（新建或整体覆盖），必须在 Harness 中得到用户批准后写入；最多 512 KiB。修改已有文件请优先使用 edit_file，避免整文件重写。',
    { path: { type: 'string', required: true }, content: { type: 'string', required: true } }],
  ['edit_file', '精确修改已有文本文件：old_text 必须在最新文本（含未保存的编辑器缓冲区）中唯一精确匹配（自动兼容 Windows/Linux 换行差异），替换为 new_text；replace_all=true 时替换全部匹配。匹配失败或出现多处匹配会给出中文错误。用户批准后才写入，其余内容保持不变。',
    { path: { type: 'string', required: true }, old_text: { type: 'string', required: true, description: '要替换的原文，必须唯一精确匹配（含空格、缩进与换行符）' }, new_text: { type: 'string', required: true, description: '替换后的文本；为空字符串表示删除这段文字' }, replace_all: { type: 'boolean', description: '是否替换全部匹配，默认 false' } }],
  ['create_file', '新建文件（仅工作区模式）：path 不能已存在，content 为完整内容；单文件模式不可用。用户批准后才写入；最多 512 KiB。',
    { path: { type: 'string', required: true }, content: { type: 'string', required: true } }],
];
const writeTools = new Set(['write_file', 'edit_file', 'create_file']);

export function apply(ctx) {
  // Transport-neutral: the WebView launcher installs the legacy QINGYE line
  // bridge, the SDK launcher installs the loopback TCP client.
  const bridge = getNativeBridge();
  ctx.on('agent/created', async ({ agent }) => {
    await bridge('pin', { sessionId: agent.session.id, args: { cwd: ctx.workingDirectory.get(agent.session) } });
  });
  for (const [name, description, parameters] of tools) {
    ctx.effect(() => ctx.tools.register(defineTool({
      name, description, parameters,
      output: { schema: { type: 'string' }, render: (_args, value) => [{ type: 'text', text: value }] },
      async execute(args, exec) {
        if (!exec.agent) throw new Error('文件工具需要活动会话');
        const payload = { sessionId: exec.agent.session.id, name, args };
        const value = await bridge('tool', payload, exec.signal);
        if (!writeTools.has(name)) return value;
        try {
          if (value.requiresApproval !== false) {
            // Approval travels the same loopback bridge as the proposal: the
            // editor shows its native diff card and answers {allowed}. A
            // disconnected or timed-out bridge fails closed, never open.
            const approval = await bridge('approval', {
              sessionId: exec.agent.session.id,
              name,
              path: args.path,
              diff: value.diff,
              proposalId: value.id,
              reason: `${EditLabel(name, args.path)}。确认后写入磁盘。`,
            }, exec.signal).catch(error => {
              if (exec.signal?.aborted) throw error;
              return { allowed: false, error: String(error?.message ?? error) };
            });
            if (approval?.allowed !== true) return '用户未批准，文件未修改。';
          }
          exec.signal?.throwIfAborted();
          return await bridge('apply', { ...payload, proposal: value.id }, exec.signal);
        } finally {
          await bridge('discard', { ...payload, proposal: value.id }).catch(() => {});
        }
      },
    })));
  }
  // Other installed plugins cannot enlarge the editor's file capability set.
  const allowed = new Set([...tools.map(([name]) => name), 'todo_write', 'ask_user_question', 'compact']);
  // Scoped tools are local registrations, not names accepted by restrict().
  ctx.effect(() => ctx.tools.guard(exec => allowed.has(exec.name) ? undefined : '轻页工作区沙盒未开放此工具。'));
}

function EditLabel(name, path) {
  if (name === 'edit_file') return `编辑 ${path}`;
  if (name === 'create_file') return `新建 ${path}`;
  return `修改 ${path}`;
}
