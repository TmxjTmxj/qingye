import { access, readFile } from 'node:fs/promises';
import path from 'node:path';

// The pinned CLI imports these three roots; its package manifest also lists
// every optional product profile, which this native SDK host never launches.
const CLI_ROOTS = ['@deepseek-ai/dsh-app-boot', '@deepseek-ai/dsh-home-paths', 'commander'];
const SDK_ROOTS = [
  '@deepseek-ai/dsh-sdk-minimal', '@deepseek-ai/dsh-llm-pi-ai',
  '@deepseek-ai/dsh-sdk-jsonrpc-server', '@deepseek-ai/dsh-sdk-protocol',
  '@deepseek-ai/dsh-session-persistence', '@deepseek-ai/dsh-storage',
  '@deepseek-ai/dsh-tools', '@deepseek-ai/cordis',
];

export async function sdkPackageRoots(nodeModules) {
  const source = path.resolve(nodeModules);
  const parent = path.dirname(source);
  const selected = new Set();
  const exists = file => access(file).then(() => true, () => false);
  async function locate(name, from) {
    for (let directory = from; directory.length >= parent.length; directory = path.dirname(directory)) {
      const candidate = path.join(directory, 'node_modules', name, 'package.json');
      if (await exists(candidate)) return path.dirname(candidate);
      if (path.dirname(directory) === directory) break;
    }
    return undefined;
  }
  async function visit(directory, expand = true) {
    const relative = path.relative(source, directory).replaceAll('\\', '/');
    if (relative.startsWith('../') || path.isAbsolute(relative)) throw new Error('SDK dependency escaped node_modules');
    if (selected.has(relative)) return;
    selected.add(relative);
    if (!expand) return;
    const manifest = JSON.parse(await readFile(path.join(directory, 'package.json'), 'utf8'));
    const required = Object.keys(manifest.dependencies ?? {});
    const optional = new Set([...Object.keys(manifest.optionalDependencies ?? {}), ...Object.keys(manifest.peerDependencies ?? {})]);
    for (const name of new Set([...required, ...optional])) {
      const target = await locate(name, directory);
      if (target) await visit(target);
      else if (!optional.has(name)) throw new Error(`Required SDK dependency is missing: ${name}`);
    }
  }
  const cli = path.join(source, '@deepseek-ai/dsh');
  const manifest = JSON.parse(await readFile(path.join(cli, 'package.json'), 'utf8'));
  if (manifest.version !== '0.2.1-rc.5') throw new Error('Review the CLI runtime imports before changing its pinned version');
  await visit(cli, false);
  for (const name of [...CLI_ROOTS, ...SDK_ROOTS]) {
    const directory = await locate(name, parent);
    if (!directory) throw new Error(`SDK root is missing: ${name}`);
    await visit(directory);
  }
  return selected;
}

export function belongsToSdkPackage(relative, selected) {
  if (!relative) return true;
  const parts = relative.replaceAll('\\', '/').split('/');
  let start = 0;
  for (let index = parts.length - 1; index >= 0; index--)
    if (parts[index] === 'node_modules') { start = index + 1; break; }
  const count = parts[start]?.startsWith('@') ? 2 : 1;
  if (parts.length < start + count)
    return [...selected].some(root => root.startsWith(parts.join('/') + '/'));
  return selected.has(parts.slice(0, start + count).join('/'));
}
