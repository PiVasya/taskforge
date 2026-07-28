import React from 'react';
import { ExternalLink } from 'lucide-react';
import { Button, Card } from '../../../components/ui';
import PublicProfileCard from '../../../components/profile/PublicProfileCard';

function ProfilePreviewSettingsSection({ loading, profile, profileId, previewProfile }) {
  if (loading && !profile) return <Card className="p-4 text-sm text-neutral-500 dark:text-neutral-400">Загрузка публичной страницы…</Card>;
  if (!profile) return <Card className="p-4 text-sm text-neutral-500 dark:text-neutral-400">Профиль не загрузился.</Card>;
  return (
    <div className="space-y-4">
      <Card className="p-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div><div className="font-semibold">Предпросмотр публичной страницы</div></div>
        <Button type="button" variant="outline" disabled={!profileId} onClick={() => profileId && window.open(`/users/${profileId}`, '_blank', 'noopener,noreferrer')}>
          <ExternalLink size={16} /><span>Открыть отдельно</span>
        </Button>
      </Card>
      <PublicProfileCard profile={previewProfile} embedded />
    </div>
  );
}

export default React.memo(ProfilePreviewSettingsSection);
