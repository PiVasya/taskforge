const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const source = path.join(root, 'node_modules', 'monaco-editor', 'min', 'vs');
const target = path.join(root, 'public', 'monaco', 'vs');

if (!fs.existsSync(source)) {
  throw new Error(`Monaco Editor files were not found: ${source}`);
}

fs.rmSync(target, { recursive: true, force: true });
fs.mkdirSync(path.dirname(target), { recursive: true });
fs.cpSync(source, target, { recursive: true });

console.log(`Monaco Editor copied to ${target}`);
