import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Card, Button, Input, Select } from '../../components/ui';
import { searchUsersOnce } from '../../api/admin';
import {
  getAllBadges,
  createBadge,
  awardBadge,
  deleteBadge,
  getUserBadges,
  revokeBadge,
} from '../../api/badges';

/**
 * Страница администрирования бейджей.
 * Позволяет:
 *  – загрузить новый бейдж (SVG-файл) с названием и описанием;
 *  – просмотреть список существующих бейджей;
 *  – найти пользователя и выдать ему выбранный бейдж.
 */
export default function AdminBadgesPage() {
  // поиск пользователя
  const [q, setQ] = useState('');
  const [users, setUsers] = useState([]);
  const [userId, setUserId] = useState('');
  const [searchLoading, setSearchLoading] = useState(false);

  // список бейджей
  const [badges, setBadges] = useState([]);
  const [badgesLoading, setBadgesLoading] = useState(true);

  // бейджи выбранного пользователя
  const [userBadges, setUserBadges] = useState([]);
  const [userBadgesLoading, setUserBadgesLoading] = useState(false);

  // создание бейджа
  const [newName, setNewName] = useState('');
  const [newDesc, setNewDesc] = useState('');
  const [newFile, setNewFile] = useState(null);
  const [uploading, setUploading] = useState(false);

  // сообщения
  const [message, setMessage] = useState('');

  useEffect(() => {
    // загружаем список бейджей при монтировании
    (async () => {
      await loadBadges();
    })();
  }, []);

  // загружаем бейджи выбранного пользователя при смене userId
  useEffect(() => {
    if (!userId) {
      setUserBadges([]);
      return;
    }
    (async () => {
      await loadUserBadges(userId);
    })();
  }, [userId]);

  /**
   * Загрузить список доступных бейджей с бэка
   */
  const loadBadges = async () => {
    setBadgesLoading(true);
    try {
      const list = await getAllBadges();
      setBadges(Array.isArray(list) ? list : []);
    } catch (err) {
      console.error('Failed to load badges', err);
    } finally {
      setBadgesLoading(false);
    }
  };

  /**
   * Загрузить список бейджей выбранного пользователя
   * @param {string} uid
   */
  const loadUserBadges = async (uid) => {
    if (!uid) return;
    setUserBadgesLoading(true);
    try {
      const list = await getUserBadges(uid);
      setUserBadges(Array.isArray(list) ? list : []);
    } catch (err) {
      console.error('Failed to load user badges', err);
    } finally {
      setUserBadgesLoading(false);
    }
  };

  /**
   * Поиск пользователей по строке q
   */
  const loadUsers = async () => {
    setSearchLoading(true);
    try {
      const data = await searchUsersOnce(q, 20);
      setUsers(data || []);
    } catch (e) {
      console.error('Failed to search users', e);
    } finally {
      setSearchLoading(false);
    }
  };

  /**
   * Создание нового бейджа
   */
  const handleCreateBadge = async () => {
    if (!newName || !newFile) return;
    const formData = new FormData();
    formData.append('name', newName);
    if (newDesc) formData.append('description', newDesc);
    formData.append('file', newFile);
    try {
      setUploading(true);
      await createBadge(formData);
      setNewName('');
      setNewDesc('');
      setNewFile(null);
      await loadBadges();
      setMessage('Бейдж создан');
    } catch (err) {
      console.error('Failed to create badge', err);
      setMessage('Не удалось создать бейдж');
    } finally {
      setUploading(false);
    }
  };

  /**
   * Выдача выбранного бейджа выбранному пользователю
   */
  const handleAward = async (badgeId) => {
    if (!userId) {
      setMessage('Сначала выберите пользователя');
      return;
    }
    try {
      await awardBadge(userId, badgeId);
      setMessage('Бейдж выдан');
      // обновляем список бейджей пользователя
      await loadUserBadges(userId);
    } catch (err) {
      console.error('Failed to award badge', err);
      setMessage('Не удалось выдать бейдж');
    }
  };

  /**
   * Удаление бейджа
   */
  const handleDelete = async (badgeId) => {
    const ok = window.confirm('Удалить этот бейдж?');
    if (!ok) return;
    try {
      await deleteBadge(badgeId);
      await loadBadges();
      setMessage('Бейдж удалён');
    } catch (err) {
      console.error('Failed to delete badge', err);
      setMessage('Не удалось удалить бейдж');
    }
  };

  /**
   * Снятие (удаление) бейджа у выбранного пользователя
   */
  const handleRevoke = async (badgeId) => {
    if (!userId) {
      setMessage('Сначала выберите пользователя');
      return;
    }
    const ok = window.confirm('Снять этот бейдж у пользователя?');
    if (!ok) return;
    try {
      await revokeBadge(userId, badgeId);
      setMessage('Бейдж снят');
      await loadUserBadges(userId);
    } catch (err) {
      console.error('Failed to revoke badge', err);
      setMessage('Не удалось снять бейдж');
    }
  };

  const selectedUser = users.find((u) => u.id === userId) || null;

  return (
    <Layout>
      <div className="container-app py-6 space-y-4">
        <h1 className="text-2xl font-semibold">Бейджи</h1>

        {/* поиск и выбор пользователя */}
        <Card className="p-4 space-y-3">
          <div className="flex flex-wrap gap-3 items-end">
            <div className="space-y-1">
              <div className="text-xs uppercase tracking-wide text-slate-500">
                Поиск пользователя
              </div>
              <div className="flex gap-2">
                <Input
                  placeholder="email / имя / фамилия"
                  value={q}
                  onChange={(e) => setQ(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') loadUsers();
                  }}
                />
                <Button onClick={loadUsers} disabled={searchLoading}>
                  Найти
                </Button>
              </div>
            </div>
            <div className="space-y-1 min-w-[220px]">
              <div className="text-xs uppercase tracking-wide text-slate-500">Пользователь</div>
              <Select value={userId} onChange={(e) => setUserId(e.target.value)}>
                <option value="">— не выбрано —</option>
                {users.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.email} ({u.firstName} {u.lastName})
                  </option>
                ))}
              </Select>
            </div>
          </div>
          {selectedUser && (
            <div className="text-xs text-slate-600 dark:text-slate-300">
              Выбран: <span className="font-mono">{selectedUser.email}</span>
            </div>
          )}
        </Card>

        {/* создание нового бейджа */}
        <Card className="p-4 space-y-4">
          <h2 className="text-lg font-semibold">Создать новый бейдж</h2>
          <div className="grid gap-4 md:grid-cols-3">
            <div>
              <div className="text-sm text-slate-500">Название</div>
              <Input
                placeholder="Название бейджа"
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
              />
            </div>
            <div>
              <div className="text-sm text-slate-500">Описание</div>
              <Input
                placeholder="Описание (необязательно)"
                value={newDesc}
                onChange={(e) => setNewDesc(e.target.value)}
              />
            </div>
            <div>
              <div className="text-sm text-slate-500">SVG-файл</div>
              <input
                type="file"
                accept="image/svg+xml"
                onChange={(e) => {
                  if (e.target.files && e.target.files.length > 0) {
                    setNewFile(e.target.files[0]);
                  } else {
                    setNewFile(null);
                  }
                }}
                className="block w-full text-sm text-slate-700 dark:text-slate-200 border border-slate-300 dark:border-slate-700 rounded-lg cursor-pointer focus:outline-none file:bg-slate-100 dark:file:bg-slate-800 file:border-0 file:rounded file:px-2 file:py-1 file:mr-2"
              />
            </div>
          </div>
          <div className="flex justify-end">
            <Button
              onClick={handleCreateBadge}
              disabled={uploading || !newName || !newFile}
            >
              {uploading ? 'Сохранение…' : 'Создать'}
            </Button>
          </div>
        </Card>

        {/* список бейджей и выдача */}
        <Card className="p-4 space-y-4">
          <h2 className="text-lg font-semibold">Список бейджей</h2>
          {badgesLoading ? (
            <div className="text-slate-600 dark:text-slate-300">Загрузка…</div>
          ) : badges.length === 0 ? (
            <div className="text-slate-600 dark:text-slate-300">Пока нет созданных бейджей</div>
          ) : (
            <div className="space-y-3">
              {badges.map((b) => (
                <div
                  key={b.id}
                  className="flex items-center justify-between border border-slate-200 dark:border-slate-800/40 rounded-xl p-3 bg-[rgb(var(--card))]"
                >
                  <div className="flex items-center gap-3 min-w-0">
                    {b.imageUrl && (
                      <img
                        src={b.imageUrl}
                        alt={b.name}
                        className="h-10 w-10 rounded border border-slate-200 dark:border-slate-700 object-contain"
                      />
                    )}
                    <div className="min-w-0">
                      <div className="font-medium text-slate-900 dark:text-slate-50 truncate">
                        {b.name}
                      </div>
                      {b.description && (
                        <div className="text-xs text-slate-600 dark:text-slate-400 truncate">
                          {b.description}
                        </div>
                      )}
                    </div>
                  </div>
                <div className="flex gap-2">
                  <Button
                    size="sm"
                    onClick={() => handleAward(b.id)}
                    disabled={!userId}
                  >
                    Назначить
                  </Button>
                  <Button
                    size="sm"
                    intent="danger"
                    onClick={() => handleDelete(b.id)}
                  >
                    Удалить
                  </Button>
                </div>
                </div>
              ))}
            </div>
          )}
        </Card>

        {message && (
          <div className="text-sm text-emerald-600 dark:text-emerald-400">{message}</div>
        )}

        {/* бейджи выбранного пользователя */}
        {selectedUser && (
          <Card className="p-4 space-y-4">
            <h2 className="text-lg font-semibold">
              Бейджи пользователя
              {selectedUser.firstName || selectedUser.lastName
                ? `: ${selectedUser.firstName} ${selectedUser.lastName}`
                : ''}
            </h2>
            {userBadgesLoading ? (
              <div className="text-slate-600 dark:text-slate-300">Загрузка…</div>
            ) : userBadges.length === 0 ? (
              <div className="text-slate-600 dark:text-slate-300">У пользователя пока нет бейджей</div>
            ) : (
              <div className="space-y-3">
                {userBadges.map((b) => (
                  <div
                    key={b.id}
                    className="flex items-center justify-between border border-slate-200 dark:border-slate-800/40 rounded-xl p-3 bg-[rgb(var(--card))]"
                  >
                    <div className="flex items-center gap-3 min-w-0">
                      {b.imageUrl && (
                        <img
                          src={b.imageUrl}
                          alt={b.name}
                          className="h-8 w-8 rounded border border-slate-200 dark:border-slate-700 object-contain"
                        />
                      )}
                      <div className="min-w-0">
                        <div className="font-medium text-slate-900 dark:text-slate-50 truncate">
                          {b.name}
                        </div>
                        {b.description && (
                          <div className="text-xs text-slate-600 dark:text-slate-400 truncate">
                            {b.description}
                          </div>
                        )}
                      </div>
                    </div>
                    <div className="flex gap-2">
                      <Button size="sm" intent="danger" onClick={() => handleRevoke(b.id)}>
                        Снять
                      </Button>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </Card>
        )}
      </div>
    </Layout>
  );
}