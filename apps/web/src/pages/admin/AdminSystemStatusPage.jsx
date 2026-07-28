import React, { useEffect, useMemo, useState } from 'react';
import { Activity, AlertTriangle, RefreshCw, ServerCrash, ShieldCheck } from 'lucide-react';
import { Button, Card } from '../../components/ui';
import { getSystemStatus } from '../../api/systemStatus';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';

function StatCard({ label, value, className = '' }) {
  return (
    <Card className={`p-5 ${className}`}>
      <div className="text-sm text-neutral-500">{label}</div>
      <div className="mt-2 text-4xl font-semibold">{value}</div>
    </Card>
  );
}

export default function AdminSystemStatusPage() {
  const notify = useNotify();
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState('');
  const [data, setData] = useState(null);

  const load = async (silent = false) => {
    try {
      if (!silent) setLoading(true);
      const res = await getSystemStatus();
      setData(res);
      setPageError('');
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось проверить статус компонентов');
      setPageError(parsed?.userMessage || 'Не удалось проверить статус компонентов');
    } finally {
      if (!silent) setLoading(false);
    }
  };

  useEffect(() => { load(); }, []);

  const stats = useMemo(() => {
    const items = Array.isArray(data?.components) ? data.components : [];
    return {
      total: items.length,
      healthy: items.filter((x) => x.status === 'healthy').length,
      bad: items.filter((x) => x.status !== 'healthy').length,
    };
  }, [data]);

  return (
    <>
      <div className="space-y-6">
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

        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="text-3xl font-semibold flex items-center gap-3"><Activity size={28} /> Статус компонентов</h1>
            <p className="text-neutral-500 mt-2">Проверка выполняется при открытии страницы. Сейчас отслеживается связка с Minecraft-плагином.</p>
          </div>
          <Button variant="outline" onClick={() => load()} disabled={loading}><RefreshCw size={16} /><span className="ml-1">Обновить</span></Button>
        </div>

        <div className="grid md:grid-cols-3 gap-4">
          <StatCard label="Всего компонентов" value={stats.total} />
          <StatCard label="В строю" value={stats.healthy} className="text-emerald-500" />
          <StatCard label="Проблемных" value={stats.bad} className="text-rose-500" />
        </div>

        <div className="space-y-4">
          {(data?.components || []).map((item) => (
            <Card key={item.code} className="p-5">
              <div className="flex items-start justify-between gap-4">
                <div>
                  <div className="flex items-center gap-2 text-2xl font-semibold">
                    {item.status === 'healthy' ? <ShieldCheck className="text-emerald-500" size={22} /> : <ServerCrash className="text-rose-500" size={22} />}
                    <span>{item.name}</span>
                  </div>
                  <div className="text-sm text-neutral-500 mt-2">{item.details || '—'}</div>
                  {item.endpoint && <div className="text-xs text-neutral-400 mt-2">{item.endpoint}</div>}
                </div>
                <span className={`text-xs rounded-full border px-3 py-1 ${item.status === 'healthy' ? 'border-emerald-500/30 text-emerald-500' : 'border-rose-500/30 text-rose-500'}`}>{item.status}</span>
              </div>
              <div className="mt-5 flex flex-wrap items-center justify-between gap-3 text-sm">
                <div>Задержка: <b>{item.latencyMs == null ? '—' : `${item.latencyMs} мс`}</b></div>
                <div>Проверено: <b>{item.checkedAtUtc ? new Date(item.checkedAtUtc).toLocaleString() : '—'}</b></div>
              </div>
            </Card>
          ))}
        </div>
      </div>
    </>
  );
}
