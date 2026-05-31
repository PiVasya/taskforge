
import api from './http';


export async function searchUsersOnce(q = '', take = 20) {
  const { data } = await api.get('/api/admin/solution-users', {
    params: { q, take },
  });
  return Array.isArray(data) ? data : [];
}


export async function getUserSolutions(userId, { courseId, assignmentId, skip = 0, take = 50, days = null } = {}) {
  const { data } = await api.get(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId, skip, take, days },
  });
  return Array.isArray(data) ? data : [];
}


export async function getSolutionDetails(id) {
  const { data } = await api.get(`/api/admin/solutions/${id}`);
  return data;
}


export async function deleteSolution(id) {
  await api.delete(`/api/admin/solutions/${id}`);
}


export async function getAdminUserGroupIds(userId) {
  const { data } = await api.get(`/api/admin/users/${userId}/groups`);
  return Array.isArray(data) ? data : [];
}


export async function getSolutionsDetailsBulkOrFallback(ids, { concurrency = 4 } = {}) {
  if (!Array.isArray(ids) || ids.length === 0) return [];

  const safeIds = ids.slice(0, 200);

  if (safeIds.length <= 8) {
    const results = [];
    for (const id of safeIds) {
      const dto = await getSolutionDetails(id);
      if (dto) results.push(dto);
    }
    return results;
  }

  const { default: pLimit } = await import('p-limit');
  const limit = pLimit(concurrency);

  const results = await Promise.all(
    safeIds.map((id) =>
      limit(async () => {
        const dto = await getSolutionDetails(id);
        return dto || null;
      })
    )
  );

  return results.filter(Boolean);
}


export async function deleteUserSolutions(userId, { courseId, assignmentId } = {}) {
  await api.delete(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId },
  });
}


export async function deleteUser(userId) {
  await api.delete(`/api/admin/users/${userId}`);
}



export async function getUserImageSolutions(userId, { days = null } = {}) {
  const { data } = await api.get(`/api/admin/users/${userId}/image-solutions`, {
    params: { days },
  });
  return Array.isArray(data) ? data : [];
}


export async function getAdminImageSolutionDetails(id) {
  const { data } = await api.get(`/api/admin/image-solutions/${id}`);
  return data;
}


export async function deleteAdminImageSolution(id) {
  await api.delete(`/api/admin/image-solutions/${id}`);
}
