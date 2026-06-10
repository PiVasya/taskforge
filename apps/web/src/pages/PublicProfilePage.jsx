import React, { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import Layout from "../components/Layout";
import api from "../api/http";
import { getUserBadges } from "../api/badges";
import PublicProfileCard from "../components/profile/PublicProfileCard";

export default function PublicProfilePage() {
  const { userId } = useParams();
  const [profile, setProfile] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [badges, setBadges] = useState([]);
  const [badgesLoading, setBadgesLoading] = useState(false);

  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        setLoading(true);
        setError(null);
        const { data } = await api.get(`/api/users/${userId}/public-profile`);
        if (alive) setProfile(data);
      } catch {
        if (alive) setError("Профиль не найден");
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => {
      alive = false;
    };
  }, [userId]);

  useEffect(() => {
    if (!userId) return undefined;
    let alive = true;
    (async () => {
      try {
        setBadgesLoading(true);
        const list = await getUserBadges(userId);
        if (alive) setBadges(Array.isArray(list) ? list : []);
      } catch {
        if (alive) setBadges([]);
      } finally {
        if (alive) setBadgesLoading(false);
      }
    })();
    return () => {
      alive = false;
    };
  }, [userId]);

  return (
    <Layout>
      {loading ? (
        <div className="max-w-3xl mx-auto text-sm text-neutral-500 dark:text-neutral-400">
          Загрузка…
        </div>
      ) : null}
      {error ? (
        <div className="max-w-3xl mx-auto text-sm text-red-500 bg-red-50 dark:bg-red-900/20 px-3 py-2 rounded-xl">
          {error}
        </div>
      ) : null}
      {profile ? (
        <PublicProfileCard
          profile={profile}
          badges={badges}
          badgesLoading={badgesLoading}
        />
      ) : null}
    </Layout>
  );
}
