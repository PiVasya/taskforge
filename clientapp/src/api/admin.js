import { api } from './http';

// API module for administrative functionality.
// These helpers wrap backend endpoints intended for administrators
// to manage users, solutions and leaderboards.

/**
 * Search for users by query. Accepts a search string (email, first name, last name)
 * and an optional limit on the number of returned results.
 * Note: this function should be triggered explicitly (e.g. on Enter key)
 * to avoid overwhelming the backend with requests on every keystroke.
 *
 * @param {string} q Search string
 * @param {number} take Maximum number of results to return
 * @returns {Promise<Array>} Array of user objects { id, email, firstName, lastName }
 */
export async function searchUsersOnce(q, take = 20) {
  const { data } = await api.get('/api/admin/users', { params: { q, take } });
  return data;
}

/**
 * Fetch a paginated list of solutions for a particular user. The results do not
 * include code; for details see getSolutionDetails(). You can optionally
 * filter by courseId or assignmentId and specify skip/take for pagination.
 *
 * @param {string} userId User identifier
 * @param {Object} opts Filtering and pagination options
 * @param {string} [opts.courseId] Filter by course
 * @param {string} [opts.assignmentId] Filter by assignment
 * @param {number} [opts.skip=0] Number of items to skip
 * @param {number} [opts.take=20] Number of items to take
 * @returns {Promise<Array>} List of solution summaries
 */
export async function getUserSolutions(userId, { courseId, assignmentId, skip = 0, take = 20 } = {}) {
  const { data } = await api.get(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId, skip, take },
  });
  return data;
}

/**
 * Fetch detailed information about a single solution, including source code and
 * counts of passed/failed tests.
 * @param {string} id Solution identifier
 * @returns {Promise<Object>} Detailed solution object
 */
export async function getSolutionDetails(id) {
  const { data } = await api.get(`/api/admin/solutions/${id}`);
  return data;
}

/**
 * Fetch leaderboard data for administrators. Returns a list of users with
 * their solved counts over a given time period. You can filter by courseId
 * and set the number of top users to return.
 * @param {Object} opts Options for filtering
 * @param {string} [opts.courseId] Course identifier
 * @param {number} [opts.days] Number of days to look back (undefined = all time)
 * @param {number} [opts.top=20] Number of users to return
 * @returns {Promise<Array>} List of leaderboard entries { userId, firstName, lastName, email, solved }
 */
export async function getLeaderboard({ courseId, days, top = 20 } = {}) {
  const { data } = await api.get('/api/admin/leaderboard', { params: { courseId, days, top } });
  return data;
}

/**
 * Delete all solutions for a particular user. You can optionally limit deletion
 * to a specific course or assignment. Without any filters, all solutions for
 * the user are removed.
 *
 * @param {string} userId User identifier
 * @param {Object} opts Filtering options
 * @param {string} [opts.courseId] Restrict deletion to a course
 * @param {string} [opts.assignmentId] Restrict deletion to an assignment
 */
export async function deleteUserSolutions(userId, { courseId, assignmentId } = {}) {
  await api.delete(`/api/admin/users/${userId}/solutions`, { params: { courseId, assignmentId } });
}
