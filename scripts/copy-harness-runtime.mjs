import { cp, mkdir, readdir, rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { belongsToSdkPackage, sdkPackageRoots } from './harness-sdk-packages.mjs';

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = path.join(project, 'src/Harness/node_modules');
const destination = path.join(project, 'build/harness-runtime/node_modules');

// This composition disables Office conversion and voice input. Native reader/export
// capabilities stay in WPF. Type declarations and source maps are build artifacts.
export function includeRuntimeFile(relative, selected) {
  const name = relative.replaceAll('\\', '/');
  if (/^@deepseek-ai\/libreoffice-kit-win32-x64(?:\/|$)/.test(name)) return false;
  if (/^@deepseek-ai\/dsh-(?:web(?:\/|$)|ui-[^/]+(?:\/|$)|desktop(?:\/|$))/.test(name)) return false;
  if (selected && !belongsToSdkPackage(name, selected)) return false;
  return !/\.(?:[cm]?ts|map)$/.test(name);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const selected = await sdkPackageRoots(source);
  await mkdir(destination, { recursive: true });
  await cp(source, destination, {
    recursive: true, force: true,
    filter: file => includeRuntimeFile(path.relative(source, file), selected),
  });
  // Clean only excluded files left by an earlier preparation of this generated tree.
  async function clean(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const file = path.join(directory, entry.name);
      const relative = path.relative(destination, file);
      if (relative.startsWith('..') || path.isAbsolute(relative) || entry.isSymbolicLink()) throw new Error('Unsafe generated runtime cleanup path');
      if (!includeRuntimeFile(relative, selected)) await rm(file, { recursive: entry.isDirectory(), force: true });
      else if (entry.isDirectory()) await clean(file);
    }
  }
  await clean(destination);
  console.log(`Copied ${selected.size} SDK runtime packages; retained package licenses.`);
}
