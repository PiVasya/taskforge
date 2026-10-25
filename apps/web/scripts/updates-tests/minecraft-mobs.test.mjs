import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const updates = new URL('../../public/updates/', import.meta.url);
const read = (filename) => JSON.parse(readFileSync(new URL(filename, updates), 'utf8'));
const main = read('minecraft-server.json');
const detail = read('minecraft-integration-mobs.json');
const index = read('index.json');
const mobsOf = (page) => page.sections.find((section) => section.type === 'mobs').items;

test('Minecraft news pages describe the same complete roster', () => {
  const a = mobsOf(main);
  const b = mobsOf(detail);
  assert.deepEqual(a, b);
  assert.equal(a.length, 15);
  assert.equal(new Set(a.map((mob) => mob.id)).size, a.length);
  assert.deepEqual(a.map((mob) => mob.index), a.map((_, i) => `${String(i + 1).padStart(2, '0')} / 15`));
  assert.equal(main.stats.find((stat) => stat.label.includes('мобов'))?.value, '15');
  assert.equal(detail.stats.find((stat) => stat.label.includes('мобов'))?.value, '15');
  assert.ok(detail.hero.badges.includes('15 изменённых мобов'));
  assert.ok(index.find((post) => post.id === 'minecraft-server').highlights.some((highlight) => highlight.includes('15 изменённых мобов')));
});

test('New combatants provide player-facing mechanics, illustrations and loot', () => {
  const mobs = new Map(mobsOf(main).map((mob) => [mob.id, mob]));
  for (const id of ['parched', 'vindicator']) {
    const mob = mobs.get(id);
    assert.ok(mob, `missing mob ${id}`);
    assert.ok(mob.image.startsWith('https://'));
    assert.ok(mob.summary.length > 30);
    assert.ok(mob.changes.length >= 3);
    assert.ok(mob.loot.length >= 3);
  }
  for (const id of ['drowned', 'illusioner', 'pillager']) {
    assert.ok(mobs.get(id).changes.length > 2, `missing combat update for ${id}`);
  }
});


test('Parched and Vindicator use ready-made GIF animations just like the other animated mobs', () => {
  for (const page of [main, detail]) {
    const mobs = new Map(mobsOf(page).map((mob) => [mob.id, mob]));
    for (const id of ['parched', 'vindicator']) {
      const mob = mobs.get(id);
      assert.equal(
        mob.animatedImage,
        `https://cdn.jsdelivr.net/gh/IsaiahPapa/minecraft-render-pipeline@main/dist/latest/${id}/spin.gif`,
      );
      assert.ok(mob.image.endsWith('/headshot.png'));
      assert.equal('replaceAnimatedFrames' in mob, false);
    }
    assert.ok(mobs.get('breeze').animatedImage.endsWith('.gif'));
    assert.ok(mobs.get('creaking').animatedImage.endsWith('.gif'));
  }

  const component = readFileSync(new URL('../../src/components/ChangelogShowcase.jsx', import.meta.url), 'utf8');
  assert.equal(component.includes('replaceFullCanvasWebpAnimationFrames'), false);
  assert.equal(component.includes('replaceAnimatedFrames'), false);
  assert.equal(component.includes('correctedAnimation'), false);
  assert.ok(component.includes('showAnimation ? mob.animatedImage : staticImage'));
});
