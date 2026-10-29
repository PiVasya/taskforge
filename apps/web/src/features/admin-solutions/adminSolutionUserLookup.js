import { searchUsersOnce } from '../../api/admin';
import { userSearchServerQueries, searchedUserSuggestions } from './adminSolutionLiveModel';

// Directory lookup remains server-side. At most three fallback queries are
// attempted after a sparse result, never a full user-directory download.
export async function searchAdminUsersSmart(query) {
  const raw = String(query || '').trim().slice(0, 100);
  if (raw.length < 2) return [];
  const alternatives = userSearchServerQueries(raw).slice(1);
  const collected = new Map();
  const add = (users) => {
    for (const user of users || []) {
      const id = String(user?.id || '').toLowerCase();
      if (id && !collected.has(id)) collected.set(id, user);
    }
  };
  add(await searchUsersOnce(raw, 30));
  const hasStrongMatch = () => searchedUserSuggestions([...collected.values()], raw, 12)
    .some((candidate) => candidate.match.score >= 740);
  if (raw.length >= 4 && collected.size < 8 && !hasStrongMatch()) {
    for (const alternate of alternatives) {
      if (collected.size >= 20 || hasStrongMatch()) break;
      try {
        add(await searchUsersOnce(alternate, 30));
      } catch {
        // An optional fallback must not discard the successful primary query.
      }
    }
  }
  // Exclude broad-prefix candidates unrelated to the original input. The API
  // still resolves identities; only a user explicitly selected by UUID is used.
  return searchedUserSuggestions([...collected.values()], raw, 40).map((item) => item.user);
}



