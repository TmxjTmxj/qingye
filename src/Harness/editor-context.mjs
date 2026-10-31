import path from 'node:path';

export const inject = ['workspaceRegistry', 'agentPresets'];

export async function apply(ctx) {
  const bridge = globalThis[Symbol.for('qingye.nativeBridge')];
  if (!bridge) throw new Error('需要由轻页启动此插件');
  const update = async context => {
    const directory = context.root || (context.file && path.dirname(context.file));
    if (directory) await ctx.workspaceRegistry.create(directory, context.root ? path.basename(directory) : `单文件 · ${path.basename(context.file)}`);
  };
  const listeners = globalThis[Symbol.for('qingye.contextListeners')];
  ctx.effect(() => { listeners.add(update); return () => listeners.delete(update); });
  await update(await bridge('context', {}));
  ctx.on('ready', async () => {
    const presets = await ctx.agentPresets.list();
    for (const preset of presets) if (preset.broken) console.log(`轻页配置诊断：${preset.broken}`);
  });
}
