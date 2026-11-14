import React from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft } from 'lucide-react';
import PublicProfileContent from '../components/profile/PublicProfileContent';

export default function PublicProfilePage() {
  const { userId } = useParams();
  return (
    <div className="mx-auto max-w-6xl space-y-5">
      <Link to="/leaderboard" className="inline-flex items-center gap-2 text-sm font-semibold text-neutral-500 transition-colors hover:text-[rgb(var(--accent))]">
        <ArrowLeft size={16} aria-hidden="true" /> К рейтингу
      </Link>
      <PublicProfileContent userId={userId} />
    </div>
  );
}
