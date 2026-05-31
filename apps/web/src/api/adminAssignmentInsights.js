import api from './http';

export async function getAdminAssignmentInsights(assignmentId) {
  const { data } = await api.get(`/api/admin/assignments/${assignmentId}/insights`);
  return data;
}
