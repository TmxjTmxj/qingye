import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const root = path.join(project, 'build/harness-runtime');
const entries = [];
async function collect(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.name === 'runtime-files.json') continue;
    const file = path.join(directory, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Runtime contains a link: ${file}`);
    if (entry.isDirectory()) await collect(file);
    else {
      const hash = createHash('sha256');
      for await (const chunk of createReadStream(file)) hash.update(chunk);
      entries.push({ path: path.relative(root, file).replaceAll('\\', '/'), sha256: hash.digest('hex') });
    }
  }
}
await collect(root);
entries.sort((left, right) => left.path.localeCompare(right.path));
await writeFile(path.join(root, 'runtime-files.json'), JSON.stringify(entries));
console.log(`Harness manifest: ${entries.length} fixed files`);
