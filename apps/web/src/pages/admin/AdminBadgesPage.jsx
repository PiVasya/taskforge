import React, { useEffect, useState } from 'react';
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
import { useNotify } from '../../components/notify/NotifyProvider';


export default function AdminBadgesPage() {
  const notify = useNotify();
  
  const [q, setQ] = useState('');
  const [users, setUsers] = useState([]);
  const [userId, setUserId] = useState('');
  const [searchLoading, setSearchLoading] = useState(false);

  
  const [badges, setBadges] = useState([]);
  const [badgesLoading, setBadgesLoading] = useState(true);

  
  const [userBadges, setUserBadges] = useState([]);
  const [userBadgesLoading, setUserBadgesLoading] = useState(false);

  
  const [newName, setNewName] = useState('');
  const [newDesc, setNewDesc] = useState('');
  const [newFile, setNewFile] = useState(null);
  const [uploading, setUploading] = useState(false);

  
  const [pageError, setPageError] = useState('');

  useEffect(() => {
    
    (async () => {
      await loadBadges();
    })();
  }, []);

  
  useEffect(() => {
    if (!userId) {
      setUserBadges([]);
      return;
    }
    (async () => {
      await loadUserBadges(userId);
    })();
  }, [userId]);

  

  const loadBadges = async () => {
    setBadgesLoading(true);
    try {
      const list = await getAllBadges();
      setBadges(Array.isArray(list) ? list : []);
      setPageError('');
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось загрузить бейджи');
      setPageError(parsed?.userMessage || 'Не удалось загрузить бейджи');
    } finally {
      setBadgesLoading(false);
    }
  };

  

  const loadUserBadges = async (uid) => {
    if (!uid) return;
    setUserBadgesLoading(true);
    try {
      const list = await getUserBadges(uid);
      setUserBadges(Array.isArray(list) ? list : []);
      setPageError('');
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось загрузить бейджи пользователя');
      setPageError(parsed?.userMessage || 'Не удалось загрузить бейджи пользователя');
    } finally {
      setUserBadgesLoading(false);
    }
  };

  

  const loadUsers = async () => {
    setSearchLoading(true);
    try {
      const data = await searchUsersOnce(q, 20);
      setUsers(data || []);
      setPageError('');
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось найти пользователей');
      setPageError(parsed?.userMessage || 'Не удалось найти пользователей');
    } finally {
      setSearchLoading(false);
    }
  };

  

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
      notify.success('Бейдж создан');
      setPageError('');
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось создать бейдж');
      setPageError(parsed?.userMessage || 'Не удалось создать бейдж');
    } finally {
      setUploading(false);
    }
  };

  

  const handleAward = async (badgeId) => {
    if (!userId) {
      notify.warn('Сначала выберите пользователя');
      return;
    }
    try {
      await awardBadge(userId, badgeId);
      notify.success('Бейдж выдан');
      setPageError('');
      await loadUserBadges(userId);
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось выдать бейдж');
      setPageError(parsed?.userMessage || 'Не удалось выдать бейдж');
    }
  };

  

  const handleDelete = async (badgeId) => {
    const ok = window.confirm('Удалить этот бейдж?');
    if (!ok) return;
    try {
      await deleteBadge(badgeId);
      await loadBadges();
      notify.success('Бейдж удалён');
      setPageError('');
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось удалить бейдж');
      setPageError(parsed?.userMessage || 'Не удалось удалить бейдж');
    }
  };

  

  const handleRevoke = async (badgeId) => {
    if (!userId) {
      notify.warn('Сначала выберите пользователя');
      return;
    }
    const ok = window.confirm('Снять этот бейдж у пользователя?');
    if (!ok) return;
    try {
      await revokeBadge(userId, badgeId);
      notify.success('Бейдж снят');
      setPageError('');
      await loadUserBadges(userId);
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось снять бейдж');
      setPageError(parsed?.userMessage || 'Не удалось снять бейдж');
    }
  };

  const selectedUser = users.find((u) => u.id === userId) || null;

  return (
    <>
      <div className="py-6 space-y-4 min-w-0">
        <h1 className="text-2xl font-semibold">Бейджи</h1>

        {pageError ? (
          <Card className="border-rose-300 bg-rose-50 text-rose-700">
            <div className="flex items-start gap-2">
              <AlertTriangle size={18} className="mt-0.5" />
              <div>
                <div className="font-medium">Не удалось загрузить админ-раздел</div>
                <div className="text-sm mt-1 whitespace-pre-wrap">{pageError}</div>
              </div>
            </div>
          </Card>
        ) : null}

        
        <Card className="p-4 space-y-3">
          <div className="grid gap-3 md:grid-cols-[minmax(0,1.1fr)_minmax(220px,0.9fr)] items-end">
            <div className="space-y-1 min-w-0">
              <div className="text-xs uppercase tracking-wide text-neutral-500">
                Поиск пользователя
              </div>
              <div className="flex flex-wrap gap-2 sm:flex-nowrap min-w-0">
                <Input
                  className="min-w-0"
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
            <div className="space-y-1 min-w-0">
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
    </>
  );
}