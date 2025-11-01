import { api } from './http';

// Helper to include bearer token in requests
function authHeaders() {
  const t = localStorage.getItem('token');
  return t ? { Authorization: `Bearer ${t}` } : {};
}

/**
 * Fetch the public leaderboard.  The backend will include personal information (email,
 * profile picture) only when the current user has admin/teacher/editor rights.
 * Accepts optional courseId, days (lookback period) and top (number of entries) parameters.
 * @param {Object} opts Query parameters
 * @param {string} [opts.courseId] Course identifier to filter by course
 * @param {number} [opts.days] Number of days to look back (e.g. 7, 30, 90)
 * @param {number} [opts.top=20] Maximum number of users to return
 */
export async function getLeaderboard({ courseId, days, top = 20 } = {}) {
  const { data } = await api.get('/api/leaderboard', {
    params: { courseId, days, top },
    headers: authHeaders(),
  });
  return data;
}
