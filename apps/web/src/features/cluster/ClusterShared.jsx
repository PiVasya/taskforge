import React, { useEffect, useRef, useState } from 'react';
import { Check, Copy, Info } from 'lucide-react';
import { bytes, DASH, percent } from './clusterModel';

export function Tag({ children, tone = 'muted', dot = false, title }) {
  return <span className={`tf-cluster-tag is-${tone}`} title={title}>{dot && <i aria-hidden="true" />}{children}</span>;
}

export function CopyValue({ value, label, compact = false }) {
  const [result, setResult] = useState('');
  const timer = useRef(null);
  useEffect(() => () => clearTimeout(timer.current), []);
  const copy = async (event) => {
    event.stopPropagation();
    try {
      await navigator.clipboard.writeText(String(value));
      setResult('Скопировано');
    } catch { setResult('Не удалось скопировать'); }
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setResult(''), 2200);
  };
  return <span className={`tf-cluster-copy nodrag nopan ${compact ? 'is-compact' : ''}`}>
    {label && <span className="tf-cluster-muted">{label}</span>}
    <code>{value || DASH}</code>
    {value && <button type="button" onClick={copy} className="tf-cluster-icon" title={result || 'Скопировать'} aria-label={`Скопировать ${label || value}`}>
      {result === 'Скопировано' ? <Check size={13} /> : <Copy size={13} />}
    </button>}
    <span className="tf-cluster-sr" role="status">{result}</span>
  </span>;
}

export function Metric({ label, value, hint }) {
  return <div className="tf-cluster-metric"><span>{label}</span><strong>{value ?? DASH}</strong>{hint && <small>{hint}</small>}</div>;
}

export function ResourceBar({ label, used, total, hint }) {
  const pct = percent(used, total);
  return <div className="tf-cluster-resource">
    <div><span>{label}</span><strong>{pct === null ? 'Нет измерения' : `${Math.round(pct)}%`}</strong></div>
    {pct !== null && <div className="tf-cluster-meter" role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(pct)}>
      <span className={pct >= 90 ? 'is-bad' : pct >= 75 ? 'is-warn' : ''} style={{ width: `${pct}%` }} />
    </div>}
    <small>{pct === null ? 'Агент не передаёт эти данные' : `${bytes(used)} из ${bytes(total)}`}{hint && pct !== null ? ` · ${hint}` : ''}</small>
  </div>;
}

export function Empty({ children, icon: Icon = Info }) {
  return <div className="tf-cluster-empty"><Icon size={24} /><span>{children}</span></div>;
}

export function Row({ label, children }) {
  return <div className="tf-cluster-row"><span>{label}</span><div>{children ?? DASH}</div></div>;
}