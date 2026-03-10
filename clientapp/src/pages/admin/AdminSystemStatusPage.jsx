import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Button, Card, Badge } from '../../components/ui';
import { Activity, RefreshCw, Server, ShieldAlert } from 'lucide-react';
import { getSystemStatus } from '../../api/systemStatus';
import { useNotify } from '../../components/notify/NotifyProvider';
import { handleApiError } from '../../utils/handleApiError';

function fmtTime(ms) {
  if (!ms) return '—';
  return new Date(ms).toLocaleString();
}

export default function AdminSystemStatusPage() {
  const notify = useNotify();
  const [components, setComponents] = useState([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  const load = async ({ silent = false } = {}) => {
    try {
      if (silent) setRefreshing(true);
      else setLoading(true);
      const data = await getSystemStatus();
      setComponents(Array.isArray(data?.components) ? data.components : []);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось получить статус компонентов');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  };

  useEffect(() => { load(); }, []); // eslint-disable-line

  const summary = useMemo(() => {
    const total = components.length;
    const healthy = components.filter((x) => x.isHealthy).length;
    return { total, healthy, unhealthy: total - healthy };
  }, [components]);

  return (
    <Layout>
      <div className="space-y-6">
        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2"><Activity size={22} /> Статус компонентов</h1>
            <p className="text-sm text-neutral-500 mt-2">Проверка выполняется при открытии страницы. Пока отслеживается только Minecraft-сервер.</p>
          </div>
          <Button variant="outline" onClick={() => load({ silent: true })} disabled={refreshing || loading}>
            <RefreshCw size={16} className={refreshing ? 'animate-spin' : ''} /> <span className="ml-1">Обновить</span>
          </Button>
        </div>

        <div className="grid md:grid-cols-3 gap-4">
          <Card><div className="text-sm opacity-70">Всего компонентов</div><div className="text-3xl font-semibold mt-2">{summary.total}</div></Card>
          <Card><div className="text-sm opacity-70">В строю</div><div className="text-3xl font-semibold mt-2 text-emerald-500">{summary.healthy}</div></Card>
          <Card><div className="text-sm opacity-70">Проблемных</div><div className="text-3xl font-semibold mt-2 text-rose-500">{summary.unhealthy}</div></Card>
        </div>

        {loading ? <div className="text-neutral-500">Проверка…</div> : (
          <div className="grid gap-4">
            {components.map((item) => (
              <Card key={item.key} className="border border-neutral-200/60 dark:border-neutral-800/60">
                <div className="flex flex-col lg:flex-row lg:items-start lg:justify-between gap-4">
                  <div className="min-w-0">
                    <div className="flex items-center gap-2 text-lg font-semibold">
                      {item.isHealthy ? <Server size={18} className="text-emerald-500" /> : <ShieldAlert size={18} className="text-rose-500" />}
                      <span>{item.name}</span>
                    </div>
                    <div className="text-sm opacity-70 mt-1">{item.endpoint || 'endpoint не задан'}</div>
                    <div className="text-sm mt-3 whitespace-pre-wrap">{item.details || 'Без подробностей.'}</div>
                  </div>
                  <div className="flex flex-col gap-2 text-sm lg:items-end">
                    <Badge intent={item.isHealthy ? 'success' : 'danger'}>{item.status}</Badge>
                    <div>Задержка: <b>{item.latencyMs != null ? `${item.latencyMs} ms` : '—'}</b></div>
                    <div>Проверено: <b>{fmtTime(item.checkedAtUnixMs)}</b></div>
                  </div>
                </div>
              </Card>
            ))}
            {components.length === 0 && <Card><div className="text-neutral-500">Компоненты пока не настроены.</div></Card>}
          </div>
        )}
      </div>
    </Layout>
  );
}
