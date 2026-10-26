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
    assert.ok(mob.image.startsWith('/updates/media/mobs/animated/'));
    assert.ok(mob.summary.length > 30);
    assert.ok(mob.changes.length >= 3);
    assert.ok(mob.loot.length >= 3);
  }
  for (const id of ['drowned', 'illusioner', 'pillager']) {
    assert.ok(mobs.get(id).changes.length > 2, `missing combat update for ${id}`);
  }
});


function readWebp(bytes) {
  assert.equal(bytes.toString('ascii', 0, 4), 'RIFF');
  assert.equal(bytes.toString('ascii', 8, 12), 'WEBP');
  assert.equal(bytes.readUInt32LE(4) + 8, bytes.length);
  const result = { frames: 0 };
  for (let offset = 12; offset + 8 <= bytes.length;) {
    const type = bytes.toString('ascii', offset, offset + 4);
    const size = bytes.readUInt32LE(offset + 4);
    const payload = offset + 8;
    assert.ok(payload + size <= bytes.length, `truncated ${type}`);
    if (type === 'VP8X') {
      result.width = bytes.readUIntLE(payload + 4, 3) + 1;
      result.height = bytes.readUIntLE(payload + 7, 3) + 1;
      result.animated = Boolean(bytes[payload] & 2);
      result.alpha = Boolean(bytes[payload] & 16);
    }
    if (type === 'ANIM') result.loop = bytes.readUInt16LE(payload + 4);
    if (type === 'ANMF') result.frames += 1;
    offset = payload + size + (size % 2);
  }
  return result;
}

test('All fifteen mobs are local, transparent, looping 512px animations on both news pages', () => {
  for (const page of [main, detail]) {
    for (const mob of mobsOf(page)) {
      assert.equal(mob.image, `/updates/media/mobs/animated/${mob.id}.webp`);
      const path = new URL(`../../public${mob.image}`, import.meta.url);
      const image = readWebp(readFileSync(path));
      assert.equal(image.width, 512, mob.id);
      assert.equal(image.height, 512, mob.id);
      assert.equal(image.animated, true, mob.id);
      assert.equal(image.alpha, true, mob.id);
      assert.ok(image.frames > 1, mob.id);
      assert.equal(image.loop, 0, mob.id);
      assert.equal(mob.fallbackImage, undefined, mob.id);
      assert.equal(mob.animatedImage, undefined, mob.id);
    }
  }
});
