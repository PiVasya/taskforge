const fs = require('fs');
const path = require('path');

const appPath = path.resolve(__dirname, '../src/App.jsx');
const caddyPath = path.resolve(__dirname, '../Caddyfile');

const appSource = fs.readFileSync(appPath, 'utf8');
const caddySource = fs.readFileSync(caddyPath, 'utf8');

const matcherLine = caddySource.match(/^\s*@spa_routes\s+path_regexp\s+taskforge_spa\s+(.+)$/m);
if (!matcherLine) {
  throw new Error('Caddyfile is missing the @spa_routes path_regexp matcher');
}

const routeMatcher = new RegExp(matcherLine[1].trim());
const routePattern = /<Route\s+[^>]*path="([^"]+)"/g;
const routes = new Set();
let match;
while ((match = routePattern.exec(appSource))) {
  if (match[1] !== '*') routes.add(match[1]);
}

function samplePath(route) {
  return route.replace(/:([A-Za-z0-9_]+)/g, (_, name) => `sample-${name.toLowerCase()}`);
}

const uncovered = [...routes].filter((route) => !routeMatcher.test(samplePath(route)));
if (uncovered.length > 0) {
  throw new Error(`Caddy SPA matcher does not cover React routes: ${uncovered.join(', ')}`);
}

const invalidSamples = [
  '/definitely-not-a-route',
  '/settings/not-a-route',
  '/admin/not-a-route',
  '/assignment/sample-id/not-a-route',
  '/courses/sample-id/not-a-route',
];
const acceptedInvalid = invalidSamples.filter((candidate) => routeMatcher.test(candidate));
if (acceptedInvalid.length > 0) {
  throw new Error(`Caddy SPA matcher accepts unknown routes: ${acceptedInvalid.join(', ')}`);
}

console.log(`SPA route/Caddy invariants OK (${routes.size} routes)`);
