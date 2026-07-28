const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..', 'src');

function fail(message) {
  console.error(`CT frontend architecture invariant failed: ${message}`);
  process.exit(1);
}

function walk(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const fullPath = path.join(directory, entry.name);
    return entry.isDirectory() ? walk(fullPath) : [fullPath];
  });
}

for (const relative of [
  'app/AppProviders.jsx',
  'app/AppShell.jsx',
  'components/shell/AppHeader.jsx',
  'components/shell/PageContent.jsx',
]) {
  if (!fs.existsSync(path.join(root, relative))) fail(`missing src/${relative}`);
}

if (fs.existsSync(path.join(root, 'components', 'Layout.jsx'))) {
  fail('legacy page-owned Layout.jsx returned');
}

for (const file of walk(path.join(root, 'pages')).filter((entry) => entry.endsWith('.jsx'))) {
  const text = fs.readFileSync(file, 'utf8');
  if (/components\/Layout|<Layout\b|<\/Layout>/.test(text)) {
    fail(`page still owns the shell: src/${path.relative(root, file)}`);
  }
}

const app = fs.readFileSync(path.join(root, 'App.jsx'), 'utf8');
if (!/lazy\(\(\) => import\(/.test(app)) fail('route-level lazy chunks are missing');
if (!/<Route element={<AppShell \/>}>/.test(app)) fail('persistent AppShell route is missing');

console.log('CT frontend architecture invariants OK');
