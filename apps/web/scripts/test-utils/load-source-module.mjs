import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const modules = new Map();
export async function loadSourceModule(url) {
  async function compile(filename) {
    if (modules.has(filename)) return modules.get(filename);
    let source = await fs.readFile(filename, 'utf8');
    const matches = [...source.matchAll(/^import\s+(?:[^;]*?\s+from\s+)?['"]([^'"]+)['"];?/gm)];
    for (const match of matches) {
      const specifier = match[1];
      if (!specifier.startsWith('.')) throw new Error(`External dependency in model test: ${specifier}`);
      let resolved = path.resolve(path.dirname(filename), specifier);
      if (!path.extname(resolved)) resolved += '.js';
      const dataUrl = await compile(resolved);
      source = source.replace(match[0], match[0].replace(specifier, dataUrl));
    }
    const result = `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;
    modules.set(filename, result);
    return result;
  }
  return import(await compile(fileURLToPath(url)));
}
