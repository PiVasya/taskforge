import React from 'react';
import { Link } from 'react-router-dom';
import { ArrowLeft } from 'lucide-react';
import { useParams } from 'react-router-dom';
import api from '../api/http';
import { getUserBadges } from '../api/badges';
import PublicProfileCard from '../components/profile/PublicProfileCard';
import PublicStatisticsDashboard from '../features/public-statistics/PublicStatisticsDashboard';
import useQuery from '../hooks/useQuery';

export default function PublicProfilePage() {
  const { userId } = useParams();
  const profileQuery = useQuery({
    queryKey: ['public-profile', userId],
    queryFn: async () => {
      const { data } = await api.get(`/api/users/${userId}/public-profile`);
      return data;
    },
    enabled: Boolean(userId),
    staleTime: 60_000,
    keepPreviousData: false,
  });
  const badgesQuery = useQuery({
    queryKey: ['user-badges', userId],
    queryFn: () => getUserBadges(userId),
    enabled: Boolean(userId),
    staleTime: 60_000,
    keepPreviousData: false,
  });

  return (
    <div className="mx-auto max-w-6xl space-y-5">
      <Link to="/leaderboard" className="inline-flex items-center gap-2 text-sm font-semibold text-neutral-500 transition-colors hover:text-[rgb(var(--accent))]">
        <ArrowLeft size={16} aria-hidden="true" /> К рейтингу
      </Link>
      {profileQuery.isLoading ? (
        <div className="max-w-3xl mx-auto text-sm text-neutral-500 dark:text-neutral-400">Загрузка…</div>
      ) : null}
      {profileQuery.error ? (
        <div className="max-w-3xl mx-auto text-sm text-red-500 bg-red-50 dark:bg-red-900/20 px-3 py-2 rounded-xl">Профиль не найден</div>
      ) : null}
      {profileQuery.data ? (
        <>
          <PublicProfileCard
            profile={profileQuery.data}
            badges={Array.isArray(badgesQuery.data) ? badgesQuery.data : []}
            badgesLoading={badgesQuery.isLoading || badgesQuery.isFetching}
            embedded
          />
          {profileQuery.data.statsVisible !== false ? (
            <PublicStatisticsDashboard
              statistics={profileQuery.data.insights}
              reliable={profileQuery.data.insightsReliable !== false}
              totalAttempts={profileQuery.data.totalAttempts}
              attemptsReliable={profileQuery.data.statsReliable !== false}
            />
          ) : null}
        </>
      ) : null}
    </div>
  );
}
