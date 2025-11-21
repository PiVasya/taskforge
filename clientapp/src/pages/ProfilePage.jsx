import React, { useEffect, useState } from 'react';
import Layout from '../components/Layout';
import { Card, Button, Input, Textarea } from '../components/ui';
import { getProfile, updateProfile } from '../api/profile';
import { parseProfileExtra, buildProfileExtra } from '../utils/profileExtra';

/**
 * Страница профиля для текущего пользователя.
 *
 * После сохранения мы не полагаемся на возвращаемое значение updateProfile,
 * потому что оно возвращает лишь булево значение. Вместо этого мы
 * обновляем локальное состояние теми же данными, что отправили на сервер,
 * чтобы поля в форме не очищались и пользователь видел актуальные значения.
 */
export default function ProfilePage() {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);
  const [saved, setSaved] = useState(false);

  const [profile, setProfile] = useState(null);
  const [extra, setExtra] = useState(parseProfileExtra(null));

  // Загрузка профиля при монтировании
  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        const data = await getProfile();
        setProfile(data);
        setExtra(parseProfileExtra(data.additionalDataJson));
      } catch (e) {
        console.error(e);
        setError('Не удалось загрузить профиль');
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  // Универсальный обработчик изменений дополнительных полей
  const handleChangeExtra = (field) => (eOrValue) => {
    const value =
      eOrValue && eOrValue.target !== undefined
        ? eOrValue.target.type === 'checkbox'
          ? eOrValue.target.checked
          : eOrValue.target.value
        : eOrValue;
    setExtra((prev) => ({
      ...prev,
      [field]: value,
    }));
  };

  // Сохранение профиля
  const handleSave = async (e) => {
    e.preventDefault();
    if (!profile) return;
    setSaving(true);
    setError(null);
    setSaved(false);
    try {
      // DTO для отправки
      const dto = {
        ...profile,
        additionalDataJson: buildProfileExtra(extra),
      };
      // Выполняем запрос на обновление; возвращаемый ответ — булево
      await updateProfile(dto);
      // Обновляем локальное состояние теми же данными
      setProfile(dto);
      setExtra(parseProfileExtra(dto.additionalDataJson));
      setSaved(true);
    } catch (err) {
      console.error(err);
      setError('Не удалось сохранить профиль');
    } finally {
      setSaving(false);
    }
  };

  return (
    <Layout>
      <div className="max-w-3xl mx-auto space-y-6">
        <h1 className="text-2xl font-semibold">Профиль</h1>
        {loading && <div>Загрузка…</div>}
        {error && (
          <div className="text-sm text-red-500 bg-red-50 dark:bg-red-900/20 px-3 py-2 rounded-xl">
            {error}
          </div>
        )}
        {saved && (
          <div className="text-sm text-emerald-600 bg-emerald-50 dark:bg-emerald-900/20 px-3 py-2 rounded-xl">
            Профиль сохранён
          </div>
        )}
        {profile && (
          <form onSubmit={handleSave} className="space-y-6">
            {/* Основное */}
            <Card className="p-4 space-y-4">
              <h2 className="font-semibold">Основное</h2>
              <div className="grid gap-4 md:grid-cols-2">
                <div>
                  <label className="text-sm text-slate-500">Имя</label>
                  <Input
                    value={profile.firstName || ''}
                    onChange={(e) =>
                      setProfile((p) => ({ ...p, firstName: e.target.value }))
                    }
                  />
                </div>
                <div>
                  <label className="text-sm text-slate-500">Фамилия</label>
                  <Input
                    value={profile.lastName || ''}
                    onChange={(e) =>
                      setProfile((p) => ({ ...p, lastName: e.target.value }))
                    }
                  />
                </div>
              </div>
              <div>
                <label className="text-sm text-slate-500">Email</label>
                <Input value={profile.email} disabled />
              </div>
            </Card>
            {/* О себе */}
            <Card className="p-4 space-y-4">
              <h2 className="font-semibold">О себе</h2>
              <div>
                <label className="text-sm text-slate-500">Краткое описание</label>
                <Textarea
                  rows={4}
                  placeholder="Например: студент ИТ, люблю C#, делаю проекты на TaskForge…"
                  value={extra.bio}
                  onChange={handleChangeExtra('bio')}
                />
              </div>
              <div className="grid gap-4 md:grid-cols-2">
                <div>
                  <label className="text-sm text-slate-500">Город / место учёбы</label>
                  <Input
                    placeholder="Минск, БГУИР, ITD-21"
                    value={extra.location}
                    onChange={handleChangeExtra('location')}
                  />
                </div>
                <div>
                  <label className="text-sm text-slate-500">Образование / группа</label>
                  <Input
                    placeholder="Факультет АИС, ITD-21"
                    value={extra.education}
                    onChange={handleChangeExtra('education')}
                  />
                </div>
              </div>
              <div>
                <label className="text-sm text-slate-500">Навыки</label>
                <Input
                  placeholder="C#, C++, SQL, React"
                  value={extra.skillsText}
                  onChange={handleChangeExtra('skillsText')}
                />
                <p className="mt-1 text-xs text-slate-400">
                  Перечисли через запятую — они будут показаны в профиле и топе.
                </p>
              </div>
            </Card>
            {/* Ссылки */}
            <Card className="p-4 space-y-4">
              <h2 className="font-semibold">Ссылки</h2>
              <div className="space-y-3">
                <div>
                  <label className="text-sm text-slate-500">GitHub</label>
                  <Input
                    placeholder="https://github.com/..."
                    value={extra.github}
                    onChange={handleChangeExtra('github')}
                  />
                </div>
                <div>
                  <label className="text-sm text-slate-500">Telegram</label>
                  <Input
                    placeholder="@ник или ссылка"
                    value={extra.telegram}
                    onChange={handleChangeExtra('telegram')}
                  />
                </div>
                <div>
                  <label className="text-sm text-slate-500">Личный сайт / портфолио</label>
                  <Input
                    placeholder="https://..."
                    value={extra.website}
                    onChange={handleChangeExtra('website')}
                  />
                </div>
              </div>
            </Card>
            {/* Переключатель показа в топе */}
            <Card className="p-4 flex items-center justify-between gap-4">
              <div>
                <div className="font-medium">Показывать меня в топе</div>
                <div className="text-xs text-slate-500">
                  Если выключить, профиль не будет отображаться в общем рейтинге.
                </div>
              </div>
              <label className="inline-flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  className="h-4 w-4 rounded border-slate-300 text-brand-600 focus:ring-brand-500"
                  checked={extra.showInLeaderboard}
                  onChange={handleChangeExtra('showInLeaderboard')}
                />
                <span className="text-sm text-slate-700 dark:text-slate-200">Включено</span>
              </label>
            </Card>
            <div className="flex justify-end">
              <Button type="submit" disabled={saving}>
                {saving ? 'Сохранение…' : 'Сохранить'}
              </Button>
            </div>
          </form>
        )}
      </div>
    </Layout>
  );
}