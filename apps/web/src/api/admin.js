
import api from './http';

function pageResult(response, requestedUserId) {
  const items = Array.isArray(response?.data) ? response.data : [];
  const rawTotal = response?.headers?.['x-total-count'];
  const parsedTotal = Number(rawTotal);
  return {
    items,
    total: Number.isFinite(parsedTotal) && parsedTotal >= 0 ? parsedTotal : items.length,
    resultUserId: String(response?.headers?.['x-result-user-id'] || requestedUserId || ''),
  };
}


export async function searchUsersOnce(q = '', take = 20) {
  const { data } = await api.get('/api/admin/solution-users', {
    params: { q, take },
  });
  return Array.isArray(data) ? data : [];
}


export async function getUserSolutionsPage(userId, { courseId, assignmentId, skip = 0, take = 50, days = null } = {}) {
  const response = await api.get(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId, skip, take, days },
  });
  return pageResult(response, userId);
}

export async function getUserSolutionsHistory(userId, { courseId, assignmentId, days = null } = {}) {
  const response = await api.get(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId, days, all: true },
  });
  return pageResult(response, userId);
}

export async function getUserSolutions(userId, options = {}) {
  const page = await getUserSolutionsPage(userId, options);
  return page.items;
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

  const workers = Math.max(1, Math.min(Number(concurrency) || 4, 8));
  const results = [];
  let cursor = 0;

  async function worker() {
    while (cursor < safeIds.length) {
      const id = safeIds[cursor];
      cursor += 1;
      const dto = await getSolutionDetails(id);
      if (dto) results.push(dto);
    }
  }

  await Promise.all(Array.from({ length: workers }, () => worker()));
  return results;
}


export async function deleteUserSolutions(userId, { courseId, assignmentId } = {}) {
  await api.delete(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId },
  });
}


export async function deleteUser(userId) {
  await api.delete(`/api/admin/users/${userId}`);
}



export async function getUserImageSolutionsPage(userId, { assignmentId = null, skip = 0, take = 50, days = null } = {}) {
  const params = { skip, take };
  if (days !== null && days !== undefined) params.days = days;
  if (assignmentId) params.assignmentId = assignmentId;
  const response = await api.get(`/api/admin/users/${userId}/image-solutions`, { params });
  return pageResult(response, userId);
}

export async function getUserImageSolutions(userId, options = {}) {
  const page = await getUserImageSolutionsPage(userId, options);
  return page.items;
}


export async function getAdminImageSolutionDetails(id) {
  const { data } = await api.get(`/api/admin/image-solutions/${id}`);
  return data;
}


export async function deleteAdminImageSolution(id) {
  await api.delete(`/api/admin/image-solutions/${id}`);
}
