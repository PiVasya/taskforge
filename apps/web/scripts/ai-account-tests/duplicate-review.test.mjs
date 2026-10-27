import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  decisionFindingStatus,
  duplicateFindingStatus,
  filterAccountReviewFindings,
} from '../../src/pages/admin/accountDuplicateReview.mjs';

const page = await readFile(new URL('../../src/pages/admin/AdminAiAccountManagerPage.jsx', import.meta.url), 'utf8');

const pair = (id, status = 'open') => ({
  id, kind: 'duplicate', status, score: 95,
  findingKey: `pair-${id}`,
  data: { accounts: [
    { userId: 'A', displayName: 'Первый', login: 'userA' },
    { userId: 'B', displayName: 'Второй', login: 'userB' },
  ] },
});
const suspicious = { id: 'S', kind: 'suspicious', status: 'open', score: 78, data: { account: { userId: 'C' } } };
const review = (findings, options = {}) => filterAccountReviewFindings({
  findings, tab: 'duplicates', query: '', localActions: {},
  verifiedIds: new Set(), blockedIds: new Set(), ...options,
});

test('duplicate tab initially shows only open pairs, not suspicious accounts or historical merges', () => {
  assert.deepEqual(review([pair('one'), suspicious, pair('two', 'merge-queued'), pair('three', 'resolved-by-merge')]).map((x) => x.id), ['one']);
});

test('verified or blocked pairs are hidden unless just handled during this review session', () => {
  assert.deepEqual(review([pair('one')], { verifiedIds: new Set(['A']), blockedIds: new Set(['B']) }), []);
  assert.deepEqual(review([pair('one', 'merge-queued')], {
    verifiedIds: new Set(['A']), blockedIds: new Set(['B']),
    localActions: { one: { status: 'merge-queued', operationId: 'job' } },
  }).map((x) => x.id), ['one']);
});

test('just-merged and just-decided rows remain in their original order after the server updates them', () => {
  const findings = [pair('first', 'confirmed'), pair('second', 'merge-queued'), pair('third')];
  const localActions = {
    first: { status: 'confirmed' },
    second: { status: 'merge-queued', operationId: 'job-1' },
  };
  assert.deepEqual(review(findings, { localActions }).map((x) => x.id), ['first', 'second', 'third']);
  assert.deepEqual(review([pair('first', 'resolved-by-merge'), ...findings.slice(1)], { localActions }).map((x) => x.id), ['first', 'second', 'third']);
});

test('search still filters local handled entries and a fresh scan does not pin older actions', () => {
  const findings = [pair('first', 'merge-queued'), pair('second')];
  const localActions = { first: { status: 'merge-queued' } };
  assert.deepEqual(review(findings, { localActions, query: 'userA' }).map((x) => x.id), ['first', 'second']);
  assert.deepEqual(review(findings, { localActions, query: 'missing name' }), []);
  assert.deepEqual(review(findings).map((x) => x.id), ['second']);
});

test('suspicious tab keeps the original open/verified filtering', () => {
  const result = filterAccountReviewFindings({
    findings: [pair('first'), suspicious], tab: 'suspicious',
    verifiedIds: new Set(['C']), blockedIds: new Set(),
    localActions: { first: { status: 'confirmed' } },
  });
  assert.deepEqual(result, []);
});

test('decision mapping and asynchronous merge progress are unambiguous', () => {
  assert.equal(decisionFindingStatus('duplicate'), 'confirmed');
  assert.equal(decisionFindingStatus('different'), 'dismissed');
  assert.equal(decisionFindingStatus('ignored'), 'ignored');
  assert.equal(duplicateFindingStatus(pair('one'), { status: 'merge-queued' }), 'merge-queued');
  assert.equal(duplicateFindingStatus(pair('one', 'merge-queued'), { status: 'merge-queued' }, { status: 'completed' }), 'resolved-by-merge');
  assert.equal(duplicateFindingStatus(pair('one', 'merge-queued'), { status: 'merge-queued' }, { status: 'failed' }), 'merge-failed');
  assert.equal(duplicateFindingStatus(pair('one', 'merge-queued'), { status: 'merge-queued' }, { status: 'cancelled' }), 'merge-cancelled');
  assert.equal(duplicateFindingStatus(pair('one', 'resolved-by-merge'), { status: 'merge-queued' }, { status: 'failed' }), 'resolved-by-merge');
});

test('compact pair header is accessible, only expanded rows contain actions and sensitive detail', () => {
  assert.match(page, /<button[\s\S]*?aria-expanded=\{expanded\}[\s\S]*?onClick=\{\(\) => onToggle\(finding\.id\)\}/);
  assert.match(page, /<DuplicateAccountPreview account=\{accounts\[0\]\}/);
  assert.match(page, /<DuplicateAccountPreview account=\{accounts\[1\]\}/);
  assert.match(page, /\{expanded \? \([\s\S]*?<AccountPanel[\s\S]*?<EvidenceList/);
  assert.match(page, /expanded=\{expandedFindingId === finding\.id\}/);
  assert.match(page, /onToggle=\{\(findingId\) => setExpandedFindingId/);
  assert.match(page, /className=\{tab === 'duplicates' \? 'space-y-1\.5' : 'space-y-4'\}/);
  assert.match(page, /status === 'confirmed' \? \([\s\S]*?handlers\.openMerge\(finding, accounts\)/);
});

test('successful merge stays on the duplicates tab and retains the row at its scroll position', () => {
  assert.match(page, /if \(value\.finding\?\.id\) acceptDuplicateDecision\(value\.finding\.id, 'merge-queued', operation\?\.id\)/);
  assert.match(page, /if \(value\.mode !== 'merge'\) setTab\(/);
  assert.match(page, /scrollAnchorRef\.current = \{[\s\S]*?findingId,[\s\S]*?top:/);
  assert.match(page, /window\.scrollBy\(0, difference\)/);
  assert.match(page, /setExpandedFindingId\(null\)/);
  assert.match(page, /const enqueueMerge = async \(value\) => \{[\s\S]*?setDialog\(null\)/);
  assert.doesNotMatch(page, /if \(success\) \{ setDialog\(null\); setTab\(/);
});

test('a successful operation is never reported as failed just because refreshing failed', () => {
  assert.match(page, /const result = await callback\(\)/);
  assert.match(page, /if \(onAccepted\) onAccepted\(result\)/);
  assert.match(page, /catch \(refreshError\)/);
  assert.match(page, /'Действие выполнено, но не удалось обновить список'/);
});

test('merge ack releases the review immediately without a blocking findings refresh', () => {
  const start = page.indexOf('const enqueueMerge = async (value) =>');
  const end = page.indexOf('const submitDialog = async (value) =>', start);
  assert.ok(start !== -1 && end > start);
  const body = page.slice(start, end);
  assert.match(body, /await createAccountOperation\(/);
  assert.match(body, /setOperations\(\(previous\) => \[operation,/);
  assert.match(body, /setDialog\(null\)/);
  assert.match(body, /finally \{ setBusy\(false\); \}/);
  assert.doesNotMatch(body, /await loadFindings|await loadReviews|await loadBlocked|await loadOperations/);
  assert.match(page, /retryOperation: retryMergeInPlace/);
});

test('pair decisions update the returned finding/review inline without making the reviewer wait for another scan', () => {
  const start = page.indexOf('const decision = async (findingId, value) =>');
  const end = page.indexOf('const verify =', start);
  assert.ok(start !== -1 && end > start);
  const body = page.slice(start, end);
  assert.match(body, /await decideAccountFinding\(findingId, value\)/);
  assert.match(body, /acceptDuplicateDecision\(findingId, decisionFindingStatus\(value\)\)/);
  assert.match(body, /setFindings\(\(current\) => current\.map/);
  assert.match(body, /setReviews\(\(current\) => \[result\.review,/);
  assert.doesNotMatch(body, /await loadFindings|await loadReviews|await loadBlocked|await loadOperations/);
});
