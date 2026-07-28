import { useMemo } from 'react';
import {
  searchUsersOnce,
  getUserSolutions,
  getAdminUserGroupIds,
  getUserImageSolutions,
} from '../../api/admin';
import { getUserTaskTestAttempts } from '../../api/taskTestAttempts';
import { getAdminGroups } from '../../api/groups';
import { getUserMathAttempts } from '../../api/mathTaskAttempts';
import useQuery from '../../hooks/useQuery';
import { useQueryClient } from '../../data/QueryClientProvider';

function asArray(value) {
  return Array.isArray(value) ? value : [];
}

export default function useAdminSolutionsData({ searchQuery, userId, filterDays }) {
  const queryClient = useQueryClient();
  const normalizedSearch = String(searchQuery || '').trim();
  const normalizedUserId = String(userId || '').trim();
  const normalizedDays = filterDays == null ? null : Number(filterDays);

  const usersKey = useMemo(
    () => ['admin-solutions', 'users', normalizedSearch],
    [normalizedSearch],
  );
  const codeKey = useMemo(
    () => ['admin-solutions', 'code', normalizedUserId, normalizedDays],
    [normalizedDays, normalizedUserId],
  );
  const testKey = useMemo(
    () => ['admin-solutions', 'tests', normalizedUserId, normalizedDays],
    [normalizedDays, normalizedUserId],
  );
  const imageKey = useMemo(
    () => ['admin-solutions', 'images', normalizedUserId, normalizedDays],
    [normalizedDays, normalizedUserId],
  );
  const mathKey = useMemo(
    () => ['admin-solutions', 'math', normalizedUserId, normalizedDays],
    [normalizedDays, normalizedUserId],
  );
  const groupsKey = useMemo(() => ['admin-solutions', 'groups'], []);
  const userGroupsKey = useMemo(
    () => ['admin-solutions', 'user-groups', normalizedUserId],
    [normalizedUserId],
  );

  const usersQuery = useQuery({
    queryKey: usersKey,
    queryFn: () => searchUsersOnce(normalizedSearch, 20),
    enabled: normalizedSearch.length > 0,
    staleTime: 30_000,
    keepPreviousData: true,
  });

  const codeQuery = useQuery({
    queryKey: codeKey,
    queryFn: () => getUserSolutions(normalizedUserId, { days: normalizedDays }),
    enabled: normalizedUserId.length > 0,
    staleTime: 15_000,
    keepPreviousData: true,
  });

  const testsQuery = useQuery({
    queryKey: testKey,
    queryFn: () => getUserTaskTestAttempts(normalizedUserId, { days: normalizedDays }),
    enabled: normalizedUserId.length > 0,
    staleTime: 15_000,
    keepPreviousData: true,
  });

  const imagesQuery = useQuery({
    queryKey: imageKey,
    queryFn: () => getUserImageSolutions(normalizedUserId, { days: normalizedDays }),
    enabled: normalizedUserId.length > 0,
    staleTime: 15_000,
    keepPreviousData: true,
  });

  const mathQuery = useQuery({
    queryKey: mathKey,
    queryFn: () => getUserMathAttempts(normalizedUserId, { days: normalizedDays }),
    enabled: normalizedUserId.length > 0,
    staleTime: 15_000,
    keepPreviousData: true,
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
    keepPreviousData: true,
  });

  const removeCodeSolution = (id) => {
    queryClient.setQueryData(codeKey, (current) => asArray(current).filter((item) => item?.id !== id));
  };
  const removeTestAttempt = (attemptId) => {
    queryClient.setQueryData(testKey, (current) => asArray(current).filter((item) => item?.attemptId !== attemptId));
  };
  const removeImageSolution = (id) => {
    queryClient.setQueryData(imageKey, (current) => asArray(current).filter((item) => item?.id !== id));
  };
  const removeMathAttempt = (attemptId) => {
    queryClient.setQueryData(mathKey, (current) => asArray(current).filter((item) => item?.attemptId !== attemptId));
  };
  const setUserGroups = (updater) => {
    queryClient.setQueryData(userGroupsKey, (current) => {
      const previous = asArray(current);
      return typeof updater === 'function' ? updater(previous) : asArray(updater);
    });
  };

  return {
    users: asArray(usersQuery.data),
    solutions: asArray(codeQuery.data),
    testAttempts: asArray(testsQuery.data),
    imageSolutions: asArray(imagesQuery.data),
    mathAttempts: asArray(mathQuery.data),
    groups: asArray(groupsQuery.data),
    userGroupIds: asArray(userGroupsQuery.data),

    searchLoading: usersQuery.isFetching,
    listLoading: codeQuery.isFetching,
    testListLoading: testsQuery.isFetching,
    imageListLoading: imagesQuery.isFetching,
    mathListLoading: mathQuery.isFetching,
    groupsLoading: groupsQuery.isFetching,

    error:
      usersQuery.error ||
      codeQuery.error ||
      testsQuery.error ||
      imagesQuery.error ||
      mathQuery.error ||
      groupsQuery.error ||
      userGroupsQuery.error ||
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
