import { getMySolutions } from '../../api/solutions';
import { getMyImageSolutions } from '../../api/imageSolutions';
import { getMyMathAttempts } from '../../api/mathTaskAttempts';
import { getMyTaskTestAttempts } from '../../api/taskTestAttempts';

// Read existing history APIs only when a local last-accepted marker is missing.
// Query by type to avoid fetching image/quiz history for a code-only course.
export async function fetchRecentSolvedForCourseMap(types = []) {
  const kinds = new Set(types);
  const jobs = [];
  if (kinds.has('code-test') || kinds.has('sql-test')) {
    jobs.push(getMySolutions({ take: 200 }).then((rows) => ({ rows, kind: 'solutions' })));
  }
  if (kinds.has('image-code') || kinds.has('image-test')) {
    jobs.push(getMyImageSolutions({ take: 100 }).then((rows) => ({ rows, kind: 'image' })));
  }
  if (kinds.has('math')) {
    jobs.push(getMyMathAttempts({ take: 100 }).then((rows) => ({ rows, kind: 'attempts' })));
  }
  if (kinds.has('test')) {
    jobs.push(getMyTaskTestAttempts({ take: 100 }).then((rows) => ({ rows, kind: 'attempts' })));
  }

  const results = await Promise.allSettled(jobs);
  const accepted = [];
  for (const result of results) {
    if (result.status !== 'fulfilled') continue;
    const { rows, kind } = result.value;
    for (const row of Array.isArray(rows) ? rows : []) {
      const passed = kind === 'attempts' || kind === 'image'
        ? row?.passed === true || row?.Passed === true
        : row?.passedAllTests === true || row?.passedAll === true || String(row?.status || row?.verdict || '').toLowerCase() === 'accepted';
      const assignmentId = String(row?.assignmentId || row?.taskAssignmentId || '').trim();
      const at = Date.parse(row?.submittedAt || row?.submittedAtUtc || row?.createdAtUtc || row?.createdAt || '');
      if (passed && assignmentId && Number.isFinite(at)) accepted.push({ assignmentId, at });
    }
  }
  return accepted.sort((a, b) => b.at - a.at);
}
