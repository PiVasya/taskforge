import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Card, Button, Input, Select } from '../../components/ui';
import { AlertTriangle } from 'lucide-react';
import { searchUsersOnce } from '../../api/admin';
import {
  getAllBadges,
  createBadge,
  awardBadge,
  deleteBadge,
  getUserBadges,
  revokeBadge,
} from '../../api/badges';
import { handleApiError } from '../../utils/handleApiError';

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
  const [pageError, setPageError] = useState('');

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
      setPageError('');
    } catch (err) {
      const parsed = handleApiError(err, { error: ()=>{}, warn: ()=>{} }, 'Не удалось загрузить бейджи');
      setPageError(parsed?.userMessage || 'Не удалось загрузить бейджи');
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
      setPageError('');
    } catch (err) {
      const parsed = handleApiError(err, { error: ()=>{}, warn: ()=>{} }, 'Не удалось загрузить бейджи пользователя');
      setPageError(parsed?.userMessage || 'Не удалось загрузить бейджи пользователя');
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
      setPageError('');
    } catch (e) {
      const parsed = handleApiError(e, { error: ()=>{}, warn: ()=>{} }, 'Не удалось найти пользователей');
      setPageError(parsed?.userMessage || 'Не удалось найти пользователей');
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
      setPageError('');
    } catch (err) {
      const parsed = handleApiError(err, { error: ()=>{}, warn: ()=>{} }, 'Не удалось создать бейдж');
      setPageError(parsed?.userMessage || 'Не удалось создать бейдж');
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
      setPageError('');
      await loadUserBadges(userId);
    } catch (err) {
      const parsed = handleApiError(err, { error: ()=>{}, warn: ()=>{} }, 'Не удалось выдать бейдж');
      setPageError(parsed?.userMessage || 'Не удалось выдать бейдж');
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
      setPageError('');
    } catch (err) {
      const parsed = handleApiError(err, { error: ()=>{}, warn: ()=>{} }, 'Не удалось удалить бейдж');
      setPageError(parsed?.userMessage || 'Не удалось удалить бейдж');
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
      setPageError('');
      await loadUserBadges(userId);
    } catch (err) {
      const parsed = handleApiError(err, { error: ()=>{}, warn: ()=>{} }, 'Не удалось снять бейдж');
      setPageError(parsed?.userMessage || 'Не удалось снять бейдж');
      setMessage('Не удалось снять бейдж');
    }
  };

  const selectedUser = users.find((u) => u.id === userId) || null;

  return (
    <Layout>
      <div className="space-y-4">
        <h1 className="text-2xl font-semibold">Бейджи</h1>

        {pageError ? (
          <Card className="border-rose-300 bg-rose-50 text-rose-700">
            <div className="flex items-start gap-2">
              <AlertTriangle size={18} className="mt-0.5" />
              <div>
                <div className="font-medium">Ошибка админ-раздела</div>
                <div className="text-sm mt-1 whitespace-pre-wrap">{pageError}</div>
              </div>
            </div>
          </Card>
        ) : null}

        {/* поиск и выбор пользователя */}
        <Card className="p-4 space-y-3">
          <div className="flex flex-wrap gap-3 items-end">
            <div className="space-y-1">
              <div className="text-xs uppercase tracking-wide text-neutral-500">
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
              <div className="text-xs uppercase tracking-wide text-neutral-500">Пользователь</div>
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
            <div className="text-xs text-neutral-600 dark:text-neutral-300">
              Выбран: <span className="font-mono">{selectedUser.email}</span>
            </div>
          )}
        </Card>

        {/* создание нового бейджа */}
        <Card className="p-4 space-y-4">
          <h2 className="text-lg font-semibold">Создать новый бейдж</h2>
          <div className="grid gap-4 md:grid-cols-3">
            <div>
              <div className="text-sm text-neutral-500">Название</div>
              <Input
                placeholder="Название бейджа"
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
              />
            </div>
            <div>
              <div className="text-sm text-neutral-500">Описание</div>
              <Input
                placeholder="Описание (необязательно)"
                value={newDesc}
                onChange={(e) => setNewDesc(e.target.value)}
              />
            </div>
            <div>
              <div className="text-sm text-neutral-500">SVG-файл</div>
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
                className="block w-full text-sm text-neutral-700 dark:text-neutral-200 border border-neutral-300 dark:border-neutral-700 rounded-lg cursor-pointer focus:outline-none file:bg-neutral-100 dark:file:bg-neutral-800 file:border-0 file:rounded file:px-2 file:py-1 file:mr-2"
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
            <div className="text-neutral-600 dark:text-neutral-300">Загрузка…</div>
          ) : badges.length === 0 ? (
            <div className="text-neutral-600 dark:text-neutral-300">Пока нет созданных бейджей</div>
          ) : (
            <div className="space-y-3">
              {badges.map((b) => (
                <div
                  key={b.id}
                  className="flex items-center justify-between border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-3 bg-[rgb(var(--card))]"
                >
                  <div className="flex items-center gap-3 min-w-0">
                    {b.imageUrl && (
                      <img
                        src={b.imageUrl}
                        alt={b.name}
                        className="h-10 w-10 rounded border border-neutral-200 dark:border-neutral-700 object-contain"
                      />
                    )}
                    <div className="min-w-0">
                      <div className="font-medium text-neutral-900 dark:text-neutral-50 truncate">
                        {b.name}
                      </div>
                      {b.description && (
                        <div className="text-xs text-neutral-600 dark:text-neutral-400 truncate">
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
              <div className="text-neutral-600 dark:text-neutral-300">Загрузка…</div>
            ) : userBadges.length === 0 ? (
              <div className="text-neutral-600 dark:text-neutral-300">У пользователя пока нет бейджей</div>
            ) : (
              <div className="space-y-3">
                {userBadges.map((b) => (
                  <div
                    key={b.id}
                    className="flex items-center justify-between border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-3 bg-[rgb(var(--card))]"
                  >
                    <div className="flex items-center gap-3 min-w-0">
                      {b.imageUrl && (
                        <img
                          src={b.imageUrl}
                          alt={b.name}
                          className="h-8 w-8 rounded border border-neutral-200 dark:border-neutral-700 object-contain"
                        />
                      )}
                      <div className="min-w-0">
                        <div className="font-medium text-neutral-900 dark:text-neutral-50 truncate">
                          {b.name}
                        </div>
                        {b.description && (
                          <div className="text-xs text-neutral-600 dark:text-neutral-400 truncate">
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