import React, { useEffect, useMemo, useState } from 'react';
import { Activity, RefreshCw, Search } from 'lucide-react';
import Layout from '../../components/Layout';
import AppErrorPanel from '../../components/AppErrorPanel';
import { Button, Card } from '../../components/ui';
import { getAdminActivity } from '../../api/adminActivity';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';

const PERIODS = [1, 7, 14, 30, 90];

function formatDateTime(value) {
  if (!value) return '—';
  return new Date(value).toLocaleString();
}

function formatNumber(value) {
  return new Intl.NumberFormat('ru-RU').format(Number(value || 0));
}

export default function AdminUserActionsPage() {
  const notify = useNotify();
  const [days, setDays] = useState(7);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [payload, setPayload] = useState(null);
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState('');
  const [source, setSource] = useState('');
  const [page, setPage] = useState(1);

  async function load(nextPage = page) {
    setLoading(true);
    setError(null);
    try {
      const data = await getAdminActivity({ days, q: query || undefined, category: category || undefined, source: source || undefined, page: nextPage, pageSize: 50 });
      setPayload(data);
      setPage(nextPage);
    } catch (e) {
      setError(handleApiError(e, notify, 'Не удалось загрузить действия пользователей'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load(1);
  }, [days, category, source]);

  const items = payload?.items || [];
  const categories = useMemo(() => (payload?.topCategories || []).map((x) => x.label), [payload]);
  const sources = useMemo(() => {
    const set = new Set((items || []).map((x) => x.source).filter(Boolean));
    return Array.from(set);
  }, [items]);

  return (
    <Layout>
      <div className="py-6 space-y-6 min-w-0">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <div className="flex items-center gap-3 text-2xl font-semibold">
              <Activity size={24} />
              <span>Действия пользователей</span>
            </div>
            <p className="mt-2 text-sm text-muted-foreground max-w-3xl">
              Журнал действий: переходы по страницам, обращения к API, отправки решений, действия в админке и другие события.
            </p>
          </div>
          <Button onClick={() => load(page)} disabled={loading} className="gap-2">
            <RefreshCw size={16} className={loading ? 'animate-spin' : ''} />
            Обновить
          </Button>
        </div>

        <div className="flex flex-wrap gap-2">
          {PERIODS.map((n) => (
            <button key={n} type="button" onClick={() => setDays(n)} className={`btn-outline ${days === n ? 'bg-[rgba(var(--accent)/0.12)] border-[rgba(var(--accent)/0.4)]' : ''}`}>
              {n} дн
            </button>
          ))}
        </div>

        {error ? <AppErrorPanel error={error} /> : null}

        <div className="grid gap-4 md:grid-cols-4">
          <Card className="p-5"><div className="text-sm text-muted-foreground">Всего действий</div><div className="mt-2 text-3xl font-semibold">{formatNumber(payload?.totals?.totalActions)}</div></Card>
          <Card className="p-5"><div className="text-sm text-muted-foreground">Уникальных пользователей</div><div className="mt-2 text-3xl font-semibold">{formatNumber(payload?.totals?.uniqueUsers)}</div></Card>
          <Card className="p-5"><div className="text-sm text-muted-foreground">Ошибочных действий</div><div className="mt-2 text-3xl font-semibold">{formatNumber(payload?.totals?.errors)}</div></Card>
          <Card className="p-5"><div className="text-sm text-muted-foreground">Переходов по страницам</div><div className="mt-2 text-3xl font-semibold">{formatNumber(payload?.totals?.navigations)}</div></Card>
        </div>

        <Card className="p-5 space-y-4">
          <div className="grid gap-3 lg:grid-cols-[1.2fr_0.6fr_0.5fr_auto]">
            <label className="relative block">
              <Search size={16} className="absolute left-4 top-1/2 -translate-y-1/2 text-muted-foreground" />
              <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Поиск по пользователю, пути, действию" className="w-full rounded-2xl border border-[rgba(var(--border)/0.55)] bg-[rgba(var(--muted)/0.22)] pl-11 pr-4 py-3 outline-none" />
            </label>
            <select value={category} onChange={(e) => setCategory(e.target.value)} className="w-full rounded-2xl border border-[rgba(var(--border)/0.55)] bg-[rgb(var(--card))] px-4 py-3 outline-none">
              <option value="">Все категории</option>
              {categories.map((x) => <option key={x} value={x}>{x}</option>)}
            </select>
            <select value={source} onChange={(e) => setSource(e.target.value)} className="w-full rounded-2xl border border-[rgba(var(--border)/0.55)] bg-[rgb(var(--card))] px-4 py-3 outline-none">
              <option value="">Все источники</option>
              {sources.map((x) => <option key={x} value={x}>{x}</option>)}
            </select>
            <Button onClick={() => load(1)} disabled={loading}>Применить</Button>
          </div>
        </Card>

        <div className="grid gap-4 xl:grid-cols-2">
          <Card className="p-5">
            <div className="mb-4 text-base font-semibold">Топ категорий</div>
            <div className="space-y-3">
              {(payload?.topCategories || []).map((item) => (
                <div key={item.label} className="flex items-center justify-between gap-3 rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.24)] px-4 py-3">
                  <div className="font-medium">{item.label}</div>
                  <div>{formatNumber(item.value)}</div>
                </div>
              ))}
            </div>
          </Card>
          <Card className="p-5">
            <div className="mb-4 text-base font-semibold">Топ действий</div>
            <div className="space-y-3">
              {(payload?.topActions || []).map((item) => (
                <div key={item.label} className="flex items-center justify-between gap-3 rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.24)] px-4 py-3">
                  <div className="font-medium">{item.label}</div>
                  <div>{formatNumber(item.value)}</div>
                </div>
              ))}
            </div>
          </Card>
        </div>

        <Card className="p-5 overflow-hidden">
          <div className="mb-4 text-base font-semibold">Лента действий</div>
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-[rgba(var(--border)/0.45)] text-left text-muted-foreground">
                  <th className="py-3 pr-3">Когда</th>
                  <th className="py-3 pr-3">Пользователь</th>
                  <th className="py-3 pr-3">Категория</th>
                  <th className="py-3 pr-3">Действие</th>
                  <th className="py-3 pr-3">Описание</th>
                  <th className="py-3 pr-3">Путь</th>
                  <th className="py-3">Статус</th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => (
                  <tr key={item.id} className="border-b border-[rgba(var(--border)/0.28)] align-top">
                    <td className="py-3 pr-3 whitespace-nowrap">{formatDateTime(item.createdAtUtc)}</td>
                    <td className="py-3 pr-3 min-w-[14rem]">
                      <div className="font-medium">{item.user?.fullName || 'Гость / система'}</div>
                      <div className="text-xs text-muted-foreground">{item.user?.email || item.source}</div>
                    </td>
                    <td className="py-3 pr-3"><span className="rounded-full border border-[rgba(var(--border)/0.45)] px-2 py-1">{item.category}</span></td>
                    <td className="py-3 pr-3">{item.actionType}</td>
                    <td className="py-3 pr-3 max-w-[28rem]">{item.description || '—'}</td>
                    <td className="py-3 pr-3 max-w-[18rem] break-all text-muted-foreground">{item.target || item.path || '—'}</td>
                    <td className="py-3 whitespace-nowrap">{item.statusCode || '—'}</td>
                  </tr>
                ))}
                {!loading && items.length === 0 ? (
                  <tr><td colSpan={7} className="py-10 text-center text-muted-foreground">Действий за выбранный период пока нет.</td></tr>
                ) : null}
              </tbody>
            </table>
          </div>
          <div className="mt-4 flex items-center justify-between gap-3">
            <div className="text-sm text-muted-foreground">Всего: {formatNumber(payload?.paging?.total)}</div>
            <div className="flex gap-2">
              <Button variant="outline" onClick={() => load(Math.max(1, page - 1))} disabled={loading || page <= 1}>Назад</Button>
              <Button variant="outline" onClick={() => load(page + 1)} disabled={loading || items.length < (payload?.paging?.pageSize || 50)}>Вперёд</Button>
            </div>
          </div>
        </Card>
      </div>
    </Layout>
  );
}
