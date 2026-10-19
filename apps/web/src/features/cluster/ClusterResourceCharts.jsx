import React, { useMemo, useState } from 'react';
import { Activity, Cpu, MemoryStick, Thermometer } from 'lucide-react';
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { bytes, dateTime } from './clusterModel';
import { metricHistory, metricsFresh } from './clusterMetrics';
import { Tag } from './ClusterShared';

const timeLabel = value => new Date(value).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

function Chart({ points, field, title, unit, icon: Icon, color, now, minutes }) {
  const values = points.filter(point => point[field] !== null);
  const last = values[values.length - 1];
  return <div className="tf-cluster-resource-chart">
    <div className="tf-cluster-chart-label"><span><Icon size={14} />{title}</span><strong>{last ? `${last[field].toFixed(1)} ${unit}` : '—'}</strong></div>
    {values.length ? <div style={{ width: '100%', height: 132 }} role="img" aria-label={`${title}: измерения сервера за ${minutes} минут`}>
      <ResponsiveContainer width="100%" height="100%">
        <LineChart data={points} margin={{ top: 8, right: 8, left: -20, bottom: 2 }} accessibilityLayer>
          <CartesianGrid stroke="var(--cl-grid)" strokeDasharray="3 3" vertical={false} />
          <XAxis dataKey="time" type="number" domain={[now - minutes * 60000, now]} tickFormatter={timeLabel} tick={{ fontSize: 9, fill: 'var(--cl-muted)' }} minTickGap={65} allowDataOverflow />
          <YAxis domain={field === 'temperature' ? [0, 110] : [0, 100]} tick={{ fontSize: 9, fill: 'var(--cl-muted)' }} width={45} />
          <Tooltip labelFormatter={timeLabel} formatter={(value, name) => [`${Number(value).toFixed(1)} ${unit}`, name]} contentStyle={{ background: 'var(--cl-surface)', borderColor: 'var(--cl-line)', fontSize: 11, borderRadius: 6 }} />
          <Line type="linear" dataKey={field} name={title} stroke={color} strokeWidth={1.6} dot={values.length === 1} connectNulls={false} isAnimationActive={false} />
          {field === 'cpu' && <Line type="linear" dataKey="steal" name="CPU steal" stroke="var(--cl-warn)" strokeWidth={1} strokeDasharray="4 3" dot={false} connectNulls={false} isAnimationActive={false} />}
        </LineChart>
      </ResponsiveContainer>
    </div> : <div className="tf-cluster-chart-empty">{field === 'temperature' ? 'Нет показаний датчика CPU' : 'Нет измерений'}</div>}
  </div>;
}

function NodeCharts({ node, now, minutes }) {
  const points = useMemo(() => metricHistory(node.host, now, minutes), [node.host, now, minutes]);
  const fresh = metricsFresh(node, now);
  const fans = Array.isArray(node.host?.fans) ? node.host.fans : [];
  return <article className="tf-cluster-resource-node" aria-label={`Нагрузка сервера ${node.id}`}>
    <header><h3>Сервер {node.id}</h3><Tag tone={fresh ? 'good' : 'muted'}>{fresh ? 'Живые измерения' : 'Нет свежих измерений'}</Tag><span>{dateTime(node.host?.sampled_at)}</span></header>
    <div className="tf-cluster-resource-grid">
      <Chart points={points} field="cpu" title="CPU" unit="%" icon={Cpu} color="var(--cl-accent)" now={now} minutes={minutes} />
      <Chart points={points} field="memory" title="Оперативная память" unit="%" icon={MemoryStick} color="var(--cl-good)" now={now} minutes={minutes} />
      <Chart points={points} field="temperature" title="Температура CPU" unit="°C" icon={Thermometer} color="var(--cl-warn)" now={now} minutes={minutes} />
    </div>
    <footer><span>{node.host?.cpu_count || '—'} логических CPU · пунктир: CPU steal</span><span>RAM: {bytes(node.host?.memory?.used)} / {bytes(node.host?.memory?.total)} · доступно {bytes(node.host?.memory?.available)}</span>{fans.length > 0 && <span>Вентилятор: {fans.map(f => `${f.rpm} RPM`).join(' / ')}</span>}</footer>
  </article>;
}

export default function ClusterResourceCharts({ nodes, now = Date.now() }) {
  const [minutes, setMinutes] = useState(15);
  return <section className="tf-cluster-panel" aria-label="Графики нагрузки серверов">
    <div className="tf-cluster-panel-head"><div className="tf-cluster-heading"><Activity size={18} /><h2>Нагрузка серверов</h2></div><div className="tf-cluster-header-actions">{[15, 60].map(value => <button key={value} type="button" className="tf-cluster-button" aria-pressed={value === minutes} onClick={() => setMinutes(value)}>{value} мин</button>)}<details className="tf-cluster-metrics-help"><summary>О метриках</summary><p>CPU — доля времени выполнения по всем логическим процессорам. Пунктир — время ожидания гипервизора. RAM — общая память минус MemAvailable. Датчики читаются на сервере. Отсутствующие измерения и разрывы связи не заменяются нулями. История сохраняется агентом, даже когда эта страница закрыта.</p></details></div></div>
    {nodes.map(node => <NodeCharts key={node.id} node={node} now={now} minutes={minutes} />)}
  </section>;
}
