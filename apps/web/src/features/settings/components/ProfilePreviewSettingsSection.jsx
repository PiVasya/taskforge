import React from 'react';
import { ExternalLink } from 'lucide-react';
import { Button, Card } from '../../../components/ui';
import PublicProfileCard from '../../../components/profile/PublicProfileCard';

function ProfilePreviewSettingsSection({ loading, profile, profileId, previewProfile, badges = [], badgesLoading = false }) {
  if (loading && !profile) return <Card className="p-4 text-sm text-neutral-500 dark:text-neutral-400">Загрузка публичной страницы…</Card>;
  if (!profile) return <Card className="p-4 text-sm text-neutral-500 dark:text-neutral-400">Профиль не загрузился.</Card>;
  return (
    <div className="space-y-4">
      <Card className="p-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <div className="font-semibold">Предпросмотр публичной страницы</div>
          {previewProfile?.publicProfileEnabled === false ? <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">Публичный профиль выключен.</div> : null}
        </div>
        <Button type="button" variant="outline" disabled={!profileId} onClick={() => profileId && window.open(`/users/${profileId}`, '_blank', 'noopener,noreferrer')}>
          <ExternalLink size={16} /><span>Открыть отдельно</span>
        </Button>
      </Card>
      {previewProfile?.publicProfileEnabled === false ? (
        <Card className="p-5 text-sm text-neutral-500 dark:text-neutral-400">После сохранения публичная страница будет недоступна другим пользователям.</Card>
      ) : (
        <PublicProfileCard profile={previewProfile} badges={badges} badgesLoading={badgesLoading} embedded />
      )}
    </div>
  );
}

export default React.memo(ProfilePreviewSettingsSection);
