// clientapp/src/pages/ProfilePage.jsx
import React, { useEffect, useState } from 'react';
import Layout from '../components/Layout';
import { Card, Button, Input, Textarea } from '../components/ui';
import { getProfile, updateProfile } from '../api/profile';
import { useNotify } from '../components/notify/NotifyProvider';
import { handleApiError } from '../utils/handleApiError';

/**
 * Page that allows the authenticated user to view and edit their profile.
 */
export default function ProfilePage() {
  const notify = useNotify();
  const [profile, setProfile] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);

  // Load profile on mount
  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        const data = await getProfile();
        setProfile(data);
      } catch (e) {
        // Use centralized error handler for API errors (will redirect on 401)
        handleApiError(e, notify, 'Не удалось загрузить профиль');
        setError(e.message || 'Не удалось загрузить профиль');
      } finally {
        setLoading(false);
      }
    })();
  }, [notify]);

  const onChange = (field, value) => {
    setProfile((prev) => ({ ...prev, [field]: value }));
  };

  const onSave = async () => {
    setSaving(true);
    setError('');
    try {
      const payload = {
        firstName: profile.firstName,
        lastName: profile.lastName,
        phoneNumber: profile.phoneNumber,
        dateOfBirth: profile.dateOfBirth,
        profilePictureUrl: profile.profilePictureUrl,
        additionalDataJson: profile.additionalDataJson,
      };
      await updateProfile(payload);
      notify.success('Профиль обновлён');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось обновить профиль');
      setError(e.message || 'Не удалось обновить профиль');
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <Layout>
        <div className="text-slate-500">Загрузка…</div>
      </Layout>
    );
  }
  if (!profile) {
    return (
      <Layout>
        <div className="text-red-500">{error || 'Профиль не найден'}</div>
      </Layout>
    );
  }

  return (
    <Layout>
      <Card>
        <h1 className="text-2xl font-semibold mb-4">Профиль</h1>
        {error && <div className="text-red-500 mb-3">{error}</div>}
        <div className="grid gap-3">
          <div>
            <label className="label">Email</label>
            <Input value={profile.email} disabled />
          </div>
          <div>
            <label className="label">Имя</label>
            <Input
              value={profile.firstName || ''}
              onChange={(e) => onChange('firstName', e.target.value)}
            />
          </div>
          <div>
            <label className="label">Фамилия</label>
            <Input
              value={profile.lastName || ''}
              onChange={(e) => onChange('lastName', e.target.value)}
            />
          </div>
          <div>
            <label className="label">Телефон</label>
            <Input
              value={profile.phoneNumber || ''}
              onChange={(e) => onChange('phoneNumber', e.target.value)}
            />
          </div>
          <div>
            <label className="label">Дата рождения</label>
            <Input
              type="date"
              value={profile.dateOfBirth ? profile.dateOfBirth.substring(0, 10) : ''}
              onChange={(e) => onChange('dateOfBirth', e.target.value)}
            />
          </div>
          <div>
            <label className="label">URL фото профиля</label>
            <Input
              value={profile.profilePictureUrl || ''}
              onChange={(e) => onChange('profilePictureUrl', e.target.value)}
            />
          </div>
          <div>
            <label className="label">Дополнительные данные (JSON)</label>
            <Textarea
              rows={4}
              value={profile.additionalDataJson || ''}
              onChange={(e) => onChange('additionalDataJson', e.target.value)}
            />
          </div>
          <Button onClick={onSave} disabled={saving}>
            {saving ? 'Сохраняю…' : 'Сохранить'}
          </Button>
        </div>
      </Card>
    </Layout>
  );
}
