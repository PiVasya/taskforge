import React, { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, ArrowRight, CheckCircle2, Cloud, Database, Repeat2, Server, ShieldCheck, X } from 'lucide-react';
import { PRIMARY_SWITCH_REASON, primarySwitchEligibility } from './clusterModel';
import { Tag } from './ClusterShared';

export default function PrimarySwitchDialog({ open, nodes, activeId, initialTarget, busy, onClose, onSubmit }) {
  const [targetId, setTargetId] = useState(null);
  const active = useMemo(() => nodes.find(node => node.id === activeId) || null, [nodes, activeId]);
  const edgeRequired = nodes.some(node => node.edge?.configured === true);
  const candidates = useMemo(() => nodes.filter(node => node.id !== activeId), [nodes, activeId]);

  useEffect(() => {
    if (!open) return;
    const preferred = candidates.find(node => node.id === initialTarget && primarySwitchEligibility(node, active, edgeRequired).ready)
      || candidates.find(node => primarySwitchEligibility(node, active, edgeRequired).ready)
      || candidates[0]
      || null;
    setTargetId(preferred?.id || null);
  }, [open, initialTarget, activeId]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!open) return undefined;
    const handler = event => {
      if (event.key === 'Escape' && !busy) onClose();
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [open, busy, onClose]);

  if (!open) return null;
  const target = candidates.find(node => node.id === targetId) || null;
  const eligibility = target ? primarySwitchEligibility(target, active, edgeRequired) : { ready: false, reasons: [] };

  return <div className="tf-cluster-dialog-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !busy) onClose(); }}>
    <section className="tf-cluster-dialog" role="dialog" aria-modal="true" aria-labelledby="cluster-primary-dialog-title">
      <div className="tf-cluster-dialog-head">
        <div className="tf-cluster-title-group"><span className="tf-cluster-server-mark"><Repeat2 size={22} /></span><div><h2 id="cluster-primary-dialog-title">Сменить Primary</h2><p>Управляемое переключение Patroni без автоматического failback.</p></div></div>
        <button type="button" className="tf-cluster-icon" aria-label="Закрыть" disabled={busy} onClick={onClose}><X size={17} /></button>
      </div>

      <div className="tf-cluster-dialog-current">
        <div><small>Сейчас</small><strong>Сервер {activeId || '—'}</strong></div><ArrowRight size={19} /><div><small>Новая Primary</small><strong>{target ? `Сервер ${target.id}` : 'Выберите сервер'}</strong></div>
      </div>

      <div className="tf-cluster-candidate-grid">
        {candidates.map(node => {
          const check = primarySwitchEligibility(node, active, edgeRequired);
          const selected = node.id === targetId;
          return <button type="button" key={node.id} className={`tf-cluster-candidate ${selected ? 'is-selected' : ''} ${check.ready ? '' : 'is-disabled'}`} aria-pressed={selected} onClick={() => setTargetId(node.id)}>
            <div className="tf-cluster-candidate-title"><span className="tf-cluster-server-letter">{node.id}</span><div><strong>Сервер {node.id}</strong><small>{String(node.appProfile || '—').toUpperCase()} · Agent r{node.bundleRevision || '—'}</small></div><Tag tone={check.ready ? 'good' : 'warn'}>{check.ready ? 'Готов' : 'Проверить'}</Tag></div>
            <div className="tf-cluster-candidate-meta"><span><Server size={13} />{node.network?.publicHost || 'Public IP —'}</span><span><Database size={13} />{node.postgres?.healthy ? 'PostgreSQL готов' : 'PostgreSQL не готов'}</span><span><ShieldCheck size={13} />{node.hotStartReady ? 'Hot start готов' : 'Hot start не готов'}</span>{edgeRequired && <span><Cloud size={13} />{node.edge?.configured ? 'Cloudflare настроен' : 'Cloudflare не настроен'}</span>}</div>
            {!check.ready && <div className="tf-cluster-candidate-reasons">{check.reasons.map(reason => <span key={reason}>{PRIMARY_SWITCH_REASON[reason] || reason}</span>)}</div>}
          </button>;
        })}
      </div>

      {target?.appProfile === 'lite' && <div className="tf-cluster-callout"><AlertTriangle size={17} /><div><strong>Сервер {target.id} останется LITE.</strong><div>Смена Primary не превращает lite-профиль в full и не запускает исключённые сервисы.</div></div></div>}
      <div className="tf-cluster-switch-chain"><CheckCircle2 size={16} /><span>После Patroni новый Primary сам активирует приложения, переключит Cloudflare, подтвердит публичный маршрут и HTTPS.</span></div>

      <div className="tf-cluster-dialog-actions">
        <button type="button" className="tf-cluster-button" disabled={busy} onClick={onClose}>Отмена</button>
        <button type="button" className="tf-cluster-button is-primary tf-cluster-switch-button" disabled={busy || !target || !eligibility.ready} onClick={() => onSubmit(target.id)}><Repeat2 size={16} />{busy ? 'Отправляем запрос…' : target ? `Переключить на сервер ${target.id}` : 'Выберите сервер'}</button>
      </div>
    </section>
  </div>;
}
