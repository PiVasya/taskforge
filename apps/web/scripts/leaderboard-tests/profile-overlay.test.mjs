import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const read = (path) => readFile(new URL(path, import.meta.url), 'utf8');
const page = await read('../../src/pages/admin/LeaderboardPage.jsx');
const card = await read('../../src/components/LeaderboardCard.jsx');
const podium = await read('../../src/components/LeaderboardPodium.jsx');
const dialog = await read('../../src/components/LeaderboardProfileDialog.jsx');
const dialogStyles = await read('../../src/components/LeaderboardProfileDialog.css');
const publicProfile = await read('../../src/components/profile/PublicProfileContent.jsx');
const publicProfilePage = await read('../../src/pages/PublicProfilePage.jsx');

test('every leaderboard profile action opens local state without unmounting the ranking', () => {
  assert.match(page, /const \[profileEntry, setProfileEntry\] = useState\(null\)/);
  assert.match(page, /const openProfile = useCallback\(\(entry, trigger\) =>/);
  assert.match(page, /setProfileEntry\(entry\)/);
  assert.match(page, /const closeProfile = useCallback\(\(\) => setProfileEntry\(null\), \[\]\)/);
  assert.match(page, /<LeaderboardProfileDialog[\s\S]*?userId=\{profileEntry\.userId\}[\s\S]*?onClose=\{closeProfile\}/);
  assert.match(page, /<LeaderboardCard entry=\{e\} onOpenProfile=\{openProfile\} \/>/);
  assert.match(page, /<LeaderboardPodium entries=\{podium\} onOpenProfile=\{openProfile\}/);
  assert.match(card, /if \(onOpenProfile\) onOpenProfile\(entry, event\?\.currentTarget\)/);
  assert.match(podium, /<LeaderboardCard entry=\{selected\} onOpenProfile=\{onOpenProfile\} \/>/);
  assert.match(podium, /if \(onOpenProfile\) onOpenProfile\(entry, sourceElement\)/);
  assert.doesNotMatch(page, /navigate\(|useNavigate\(/);
});

test('opening the profile is unrelated to fetching leaderboard energy', () => {
  assert.equal((page.match(/getLeaderboard\(/g) || []).length, 1);
  assert.match(page, /useEffect\(\(\) => \{\s*fetchEntries\(\{ reset: true \}\);\s*\}, \[fetchEntries\]\)/);
  assert.match(page, /setProfileEntry\(entry\)/);
  assert.doesNotMatch(dialog, /getLeaderboard|loadEntries|fetchEntries/);
  assert.match(dialog, /createPortal\(/);
});

test('full-screen dialog closes via close button, Escape, and outside pointer; locks scroll and traps keyboard focus', () => {
  assert.match(dialog, /role="dialog"/);
  assert.match(dialog, /aria-modal="true"/);
  assert.match(dialog, /aria-labelledby=\{titleId\}/);
  assert.match(dialog, /event\.currentTarget === event\.target\) onClose\(\)/);
  assert.match(dialog, /event\.key === 'Escape'/);
  assert.match(dialog, /aria-label="Закрыть профиль"[\s\S]*?onClick=\{onClose\}/);
  assert.match(dialog, /document\.body\.style\.overflow = 'hidden'/);
  assert.match(dialog, /document\.body\.style\.overflow = previousOverflow/);
  assert.match(dialog, /event\.key !== 'Tab'/);
  assert.match(dialog, /first\.focus\(\)/);
  assert.match(dialog, /last\.focus\(\)/);
  assert.match(dialog, /target\?\.focus\?\.\(\{ preventScroll: true \}\)/);
  assert.match(dialogStyles, /position: fixed;/);
  assert.match(dialogStyles, /inset: 0;/);
  assert.match(dialogStyles, /z-index: 3000;/);
  assert.match(dialogStyles, /height: 100%;/);
  assert.match(dialogStyles, /overflow: auto;/);
});

test('dialog and direct URL render the exact same public profile with badges, privacy, and insights', () => {
  assert.match(publicProfile, /export default function PublicProfileContent\(\{ userId \}\)/);
  assert.match(publicProfile, /api\.get\(`\/api\/users\/\$\{userId\}\/public-profile`\)/);
  assert.match(publicProfile, /queryKey: \['user-badges', userId\]/);
  assert.match(publicProfile, /<PublicProfileCard[\s\S]*?embedded/);
  assert.match(publicProfile, /profileQuery\.data\.statsVisible !== false/);
  assert.match(publicProfile, /<PublicStatisticsDashboard/);
  assert.match(publicProfilePage, /<PublicProfileContent userId=\{userId\} \/>/);
  assert.match(dialog, /React\.lazy\(\(\) => import\('\.\/profile\/PublicProfileContent'\)\)/);
  assert.match(dialog, /<PublicProfileContent userId=\{userId\} \/>/);
  assert.doesNotMatch(dialog, /navigate\(|<Link|history\.push/);
});
