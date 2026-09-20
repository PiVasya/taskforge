import { useMemo } from 'react';
import {
  searchUsersOnce,
  getUserSolutionsHistory,
  getAdminUserGroupIds,
  getUserImageSolutionsPage,
} from '../../api/admin';
import { getUserTaskTestAttemptsPage } from '../../api/taskTestAttempts';
import { getAdminGroups, getAdminGroupMemberIds } from '../../api/groups';
import { getUserMathAttemptsPage } from '../../api/mathTaskAttempts';
import useQuery from '../../hooks/useQuery';
import { useQueryClient } from '../../data/QueryClientProvider';
import {
  asAdminHistoryPage,
  exactUserHistoryPage,
  removeAdminHistoryItem,
  paginateAdminHistoryPage,
} from './adminSolutionHistoryModel';

function asArray(value) {
  return Array.isArray(value) ? value : [];
}



export default function useAdminSolutionsData({ searchQuery, userId, groupId, filterDays, pages = {}, pageSize = 50 }) {
  const queryClient = useQueryClient();
  const normalizedSearch = String(searchQuery || '').trim();
  const normalizedUserId = String(userId || '').trim();
  const normalizedGroupId = String(groupId || '').trim();
  const normalizedDays = filterDays == null ? null : Number(filterDays);
  const normalizedPageSize = Math.max(10, Math.min(Number(pageSize) || 50, 200));
  const codePage = Math.max(1, Number(pages.code) || 1);
  const testPage = Math.max(1, Number(pages.tests) || 1);
  const imagePage = Math.max(1, Number(pages.images) || 1);
  const mathPage = Math.max(1, Number(pages.math) || 1);

  const usersKey = useMemo(
    () => ['admin-solutions', 'users', normalizedSearch],
    [normalizedSearch],
  );
  const codeKey = useMemo(
    () => ['admin-solutions', 'code-history', normalizedUserId, normalizedDays],
    [normalizedDays, normalizedUserId],
  );
  const testKey = useMemo(
    () => ['admin-solutions', 'tests', normalizedUserId, normalizedDays, testPage, normalizedPageSize],
    [normalizedDays, normalizedPageSize, normalizedUserId, testPage],
  );
  const imageKey = useMemo(
    () => ['admin-solutions', 'images', normalizedUserId, normalizedDays, imagePage, normalizedPageSize],
    [imagePage, normalizedDays, normalizedPageSize, normalizedUserId],
  );
  const mathKey = useMemo(
    () => ['admin-solutions', 'math', normalizedUserId, normalizedDays, mathPage, normalizedPageSize],
    [mathPage, normalizedDays, normalizedPageSize, normalizedUserId],
  );
  const groupsKey = useMemo(() => ['admin-solutions', 'groups'], []);
  const userGroupsKey = useMemo(
    () => ['admin-solutions', 'user-groups', normalizedUserId],
    [normalizedUserId],
  );
  const selectedGroupMembersKey = useMemo(
    () => ['admin-solutions', 'group-members', normalizedGroupId],
    [normalizedGroupId],
  );

  const usersQuery = useQuery({
    queryKey: usersKey,
    queryFn: () => searchUsersOnce(normalizedSearch, 20),
    enabled: normalizedSearch.length > 0,
    staleTime: 30_000,
    keepPreviousData: false,
  });

  const codeQuery = useQuery({
    queryKey: codeKey,
    queryFn: () => getUserSolutionsHistory(normalizedUserId, { days: normalizedDays }),
    enabled: normalizedUserId.length > 0,
    staleTime: 15_000,
    keepPreviousData: false,
  });

  const testsQuery = useQuery({
    queryKey: testKey,
    queryFn: () => getUserTaskTestAttemptsPage(normalizedUserId, {
      days: normalizedDays,
      skip: (testPage - 1) * normalizedPageSize,
      take: normalizedPageSize,
    }),
    enabled: normalizedUserId.length > 0,
    staleTime: 15_000,
    keepPreviousData: false,
  });

  const imagesQuery = useQuery({
    queryKey: imageKey,
    queryFn: () => getUserImageSolutionsPage(normalizedUserId, {
      days: normalizedDays,
      skip: (imagePage - 1) * normalizedPageSize,
      take: normalizedPageSize,
    }),
    enabled: normalizedUserId.length > 0,
    staleTime: 15_000,
    keepPreviousData: false,
  });

  const mathQuery = useQuery({
    queryKey: mathKey,
    queryFn: () => getUserMathAttemptsPage(normalizedUserId, {
      days: normalizedDays,
      skip: (mathPage - 1) * normalizedPageSize,
      take: normalizedPageSize,
    }),
    enabled: normalizedUserId.length > 0,
    staleTime: 15_000,
    keepPreviousData: false,
  });

  const groupsQuery = useQuery({
    queryKey: groupsKey,
    queryFn: getAdminGroups,
    staleTime: 60_000,
    keepPreviousData: true,
  });

  const userGroupsQuery = useQuery({
    queryKey: userGroupsKey,
    queryFn: () => getAdminUserGroupIds(normalizedUserId),
    enabled: normalizedUserId.length > 0,
    staleTime: 15_000,
    keepPreviousData: false,
  });

  const selectedGroupMembersQuery = useQuery({
    queryKey: selectedGroupMembersKey,
    queryFn: () => getAdminGroupMemberIds(normalizedGroupId),
    enabled: normalizedGroupId.length > 0,
    staleTime: 15_000,
    keepPreviousData: false,
  });

  const codeHistory = normalizedUserId ? exactUserHistoryPage(codeQuery.data, normalizedUserId) : asAdminHistoryPage(null);
  const codeData = paginateAdminHistoryPage(codeHistory, codePage, normalizedPageSize);
  const testData = normalizedUserId ? exactUserHistoryPage(testsQuery.data, normalizedUserId) : asAdminHistoryPage(null);
  const imageData = normalizedUserId ? exactUserHistoryPage(imagesQuery.data, normalizedUserId) : asAdminHistoryPage(null);
  const mathData = normalizedUserId ? exactUserHistoryPage(mathQuery.data, normalizedUserId) : asAdminHistoryPage(null);

  const removeCodeSolution = (id) => {
    queryClient.setQueryData(codeKey, (current) => removeAdminHistoryItem(current, (item) => item?.id !== id));
  };
  const removeTestAttempt = (attemptId) => {
    queryClient.setQueryData(testKey, (current) => removeAdminHistoryItem(current, (item) => item?.attemptId !== attemptId));
  };
  const removeImageSolution = (id) => {
    queryClient.setQueryData(imageKey, (current) => removeAdminHistoryItem(current, (item) => item?.id !== id));
  };
  const removeMathAttempt = (attemptId) => {
    queryClient.setQueryData(mathKey, (current) => removeAdminHistoryItem(current, (item) => item?.attemptId !== attemptId));
  };
  const setUserGroups = (updater) => {
    queryClient.setQueryData(userGroupsKey, (current) => {
      const previous = asArray(current);
      return typeof updater === 'function' ? updater(previous) : asArray(updater);
    });
  };

  return {
    users: asArray(usersQuery.data),
    solutions: codeData.items,
    solutionsTotal: codeData.total,
    testAttempts: testData.items,
    testAttemptsTotal: testData.total,
    imageSolutions: imageData.items,
    imageSolutionsTotal: imageData.total,
    mathAttempts: mathData.items,
    mathAttemptsTotal: mathData.total,
    groups: asArray(groupsQuery.data),
    userGroupIds: normalizedUserId ? asArray(userGroupsQuery.data) : [],
    selectedGroupUserIds: asArray(selectedGroupMembersQuery.data),

    searchLoading: usersQuery.isFetching,
    listLoading: codeQuery.isFetching,
    testListLoading: testsQuery.isFetching,
    imageListLoading: imagesQuery.isFetching,
    mathListLoading: mathQuery.isFetching,
    groupsLoading: groupsQuery.isFetching,
    groupMembersLoading: selectedGroupMembersQuery.isFetching,

    error:
      usersQuery.error ||
      codeQuery.error ||
      testsQuery.error ||
      imagesQuery.error ||
      mathQuery.error ||
      groupsQuery.error ||
      userGroupsQuery.error ||
      selectedGroupMembersQuery.error ||
      null,

    refetchUsers: usersQuery.refetch,
    refetchSolutions: codeQuery.refetch,
    refetchTests: testsQuery.refetch,
    refetchImages: imagesQuery.refetch,
    refetchMath: mathQuery.refetch,
    refetchGroups: groupsQuery.refetch,
    refetchUserGroups: userGroupsQuery.refetch,

    removeCodeSolution,
    removeTestAttempt,
    removeImageSolution,
    removeMathAttempt,
    setUserGroups,
  };
}
