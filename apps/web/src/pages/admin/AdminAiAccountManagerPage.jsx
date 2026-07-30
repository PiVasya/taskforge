import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Bot,
  BrainCircuit,
  Check,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  ExternalLink,
  Fingerprint,
  History,
  Loader2,
  RefreshCcw,
  ScanSearch,
  ShieldCheck,
  Sparkles,
  UserCheck,
  Users,
  X,
} from 'lucide-react';
import { Badge, Button, Card, Field, Input, Select } from '../../components/ui';
import AppErrorPanel from '../../components/AppErrorPanel';
import { useNotify } from '../../components/notify/NotifyProvider';
import { handleApiError } from '../../utils/handleApiError';
import {
  decideAccount,
  decideAccountFinding,
  getAccountAnalysisFindings,
  getAccountAnalysisRun,
  getAccountReviews,
  getLatestAccountAnalysisRun,
  startAccountAnalysis,
} from '../../api/accountIntelligence';

const ACTIVE_RUN_STATUSES = new Set(['queued', 'starting', 'running']);

const formatDate = (value) => {
  if (!value) return '—';
  try { return new Date(value).toLocaleString(); } catch { return '—'; }
};

const scoreIntent = (score) => {
  if (score >= 85) return 'danger';
  if (score >= 65) return 'warning';
  if (score >= 45) return 'outline';
  return 'neutral';
};

const statusLabel = (status) => ({
  open: 'Требует проверки',
  confirmed: 'Подтверждённый дубль',
  dismissed: 'Разные люди',
  ignored: 'Игнорируется',
  verified: 'Аккаунт проверен',
}[status] || status || '—');

function Metric({ label, value, hint, icon: Icon }) {
  return (
    <Card className="min-w-0">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="text-sm text-neutral-500">{label}</div>
          <div className="mt-2 text-3xl font-semibold">{value ?? 0}</div>
          {hint ? <div className="mt-2 text-xs text-neutral-500">{hint}</div> : null}
        </div>
        {Icon ? <div className="rounded-2xl border border-[rgb(var(--border))] p-2.5"><Icon size={18} /></div> : null}
      </div>
    </Card>
  );
}

function RunProgress({ run }) {
  if (!run) return null;
  const active = ACTIVE_RUN_STATUSES.has(run.status);
  return (
    <Card>
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            {active ? <Loader2 size={18} className="animate-spin" /> : run.status === 'completed' ? <CheckCircle2 size={18} /> : <X size={18} />}
            <div className="font-semibold">
              {active ? 'Анализ аккаунтов выполняется' : run.status === 'completed' ? 'Последний анализ завершён' : 'Последний анализ завершился ошибкой'}
            </div>
            <Badge intent={run.status === 'completed' ? 'success' : run.status === 'failed' ? 'danger' : 'outline'}>{run.status}</Badge>
          </div>
          <div className="mt-1 text-sm text-neutral-500">
            Этап: {run.phase || '—'} · алгоритм {run.algorithmVersion || '—'} · создан {formatDate(run.createdAtUtc)}
          </div>
        </div>
        <div className="text-sm font-semibold">{run.progressPercent ?? 0}%</div>
      </div>
      <div className="mt-4 h-2.5 overflow-hidden rounded-full bg-[rgba(var(--border)/0.7)]">
        <div className="h-full rounded-full bg-[rgb(var(--accent-600))] transition-all duration-500" style={{ width: `${Math.max(2, run.progressPercent || 0)}%` }} />
      </div>
      {run.error?.message ? <div className="mt-3 text-sm text-red-500">{run.error.message}</div> : null}
      {run.sources?.warnings?.length ? (
        <div className="mt-3 rounded-xl border border-amber-500/30 bg-amber-500/5 p-3 text-xs text-amber-700 dark:text-amber-300">
          {run.sources.warnings.join(' · ')}
        </div>
      ) : null}
    </Card>
  );
}

function AccountBadges({ account, data }) {
  const badges = [];
  if (account.verified) badges.push(['Проверенный', 'success']);
  if (String(data?.olderUserId) === String(account.userId)) badges.push(['Старый аккаунт', 'outline']);
  if (String(data?.newerUserId) === String(account.userId)) badges.push(['Новый аккаунт', 'outline']);
  if (String(data?.currentlyActiveUserId) === String(account.userId)) badges.push(['Активнее сейчас', 'warning']);
  if (String(data?.historicallyRicherUserId) === String(account.userId)) badges.push(['Больше истории', 'outline']);
  if (String(data?.suggestedPrimaryUserId) === String(account.userId)) badges.push(['Рекомендуемый основной', 'success']);
  if (String(data?.verifiedAnchorUserId) === String(account.userId)) badges.push(['Проверенный якорь', 'success']);
  if (String(data?.suspectUserId) === String(account.userId)) badges.push(['Новый подозреваемый', 'danger']);
  return <div className="flex flex-wrap gap-1.5">{badges.map(([label, intent]) => <Badge key={label} intent={intent}>{label}</Badge>)}</div>;
}

function AccountPanel({ account, data, onVerify, busy }) {
  const groupText = (account.groups || []).map((x) => x.name || x.code).filter(Boolean).join(', ');
  const minecraftText = (account.minecraft || []).map((x) => x.playerName).filter(Boolean).join(', ');
  return (
    <div className="min-w-0 rounded-2xl border border-[rgb(var(--border))] bg-[rgba(var(--card)/0.72)] p-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0">
          <div className="truncate text-lg font-semibold">{account.displayName || account.login || account.email || 'Пользователь'}</div>
          <div className="mt-1 break-all text-xs text-neutral-500">{account.login || 'без логина'} · {account.email || 'без email'}</div>
        </div>
        <Link className="inline-flex items-center gap-1 text-sm text-[rgb(var(--accent-600))]" to={`/admin/users/${account.userId}`}>
          Открыть <ExternalLink size={14} />
        </Link>
      </div>
      <div className="mt-3"><AccountBadges account={account} data={data} /></div>
      <div className="mt-4 grid grid-cols-2 gap-3 text-sm lg:grid-cols-4">
        <div><div className="text-xs text-neutral-500">Создан</div><div className="mt-1">{formatDate(account.createdAt)}</div></div>
        <div><div className="text-xs text-neutral-500">Последняя активность</div><div className="mt-1">{formatDate(account.lastActivityAt)}</div></div>
        <div><div className="text-xs text-neutral-500">Активность сейчас</div><div className="mt-1 font-semibold">{account.activityScore ?? 0}/100</div></div>
        <div><div className="text-xs text-neutral-500">Историческая ценность</div><div className="mt-1 font-semibold">{account.historicalValueScore ?? 0}/100</div></div>
      </div>
      <div className="mt-4 space-y-1 text-xs text-neutral-500">
        <div>Действия: {account.totalMeaningfulActions ?? 0} · решено: {account.solvedCount ?? 0} · рейтинг: {account.totalScore ?? 0}</div>
        <div>Группы: {groupText || 'нет'}</div>
        <div>Minecraft: {minecraftText || 'нет'}</div>
        <div>Телефон: {account.phoneNumber || 'не указан'} · Telegram: {account.telegramUsername || 'нет'}</div>
      </div>
      {!account.verified ? (
        <Button className="mt-4 w-full sm:w-auto" variant="outline" disabled={busy} onClick={() => onVerify(account.userId)}>
          <UserCheck size={16} /> <span className="ml-1">Пометить проверенным</span>
        </Button>
      ) : null}
    </div>
  );
}

function EvidenceList({ evidence = [] }) {
  const [expanded, setExpanded] = useState(false);
  const visible = expanded ? evidence : evidence.slice(0, 6);
  return (
    <div className="space-y-2">
      {visible.map((item) => (
        <div key={item.code} className="flex gap-3 rounded-xl border border-[rgb(var(--border))] p-3">
          <div className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-[rgba(var(--accent-500)/0.10)] text-[rgb(var(--accent-600))]">
            <Fingerprint size={16} />
          </div>
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <div className="font-medium">{item.title}</div>
              <Badge intent={item.weight >= 18 ? 'danger' : item.weight >= 8 ? 'warning' : 'outline'}>вес {item.weight}</Badge>
            </div>
            <div className="mt-1 text-sm text-neutral-500">{item.detail}</div>
          </div>
        </div>
      ))}
      {evidence.length > 6 ? (
        <Button variant="ghost" onClick={() => setExpanded((value) => !value)}>
          {expanded ? <ChevronUp size={16} /> : <ChevronDown size={16} />}
          <span className="ml-1">{expanded ? 'Свернуть' : `Показать все улики (${evidence.length})`}</span>
        </Button>
      ) : null}
    </div>
  );
}

function DuplicateFinding({ finding, onDecision, onVerify, busy, verifiedIds }) {
  const rawData = finding.data || {};
  const accounts = (rawData.accounts || []).map((account) => ({
    ...account,
    verified: account.verified || verifiedIds.has(String(account.userId)),
  }));
  const verifiedAccounts = accounts.filter((account) => account.verified);
  const data = { ...rawData, accounts };
  if (verifiedAccounts.length === 1) {
    data.verifiedAnchorUserId = verifiedAccounts[0].userId;
    data.suspectUserId = accounts.find((account) => !account.verified)?.userId || rawData.suspectUserId;
  }
  const accountName = (userId) => {
    const account = accounts.find((item) => String(item.userId) === String(userId));
    return account?.displayName || account?.login || account?.email || '—';
  };
  const summary = [
    ['Старый аккаунт', accountName(data.olderUserId)],
    ['Новый аккаунт', accountName(data.newerUserId)],
    ['Активнее сейчас', accountName(data.currentlyActiveUserId)],
    ['Больше полезной истории', accountName(data.historicallyRicherUserId)],
    ['Рекомендуемый основной', accountName(data.suggestedPrimaryUserId)],
  ];
  return (
    <Card>
      <div className="flex flex-col gap-3 xl:flex-row xl:items-start xl:justify-between">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <Badge intent={scoreIntent(finding.score)}>{finding.score}%</Badge>
            <div className="text-lg font-semibold">Возможный дубль</div>
            <Badge intent="outline">{statusLabel(finding.status)}</Badge>
            {data.learning?.active ? <Badge intent="success">адаптивная модель · {data.learning.labels} решений</Badge> : <Badge intent="outline">базовая модель</Badge>}
          </div>
          <div className="mt-2 text-sm text-neutral-500">
            Вероятность модели: {Math.round((finding.modelProbability || 0) * 100)}% · разница регистрации: {data.registrationGapDays ?? '—'} дн.
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button disabled={busy || finding.status === 'confirmed'} onClick={() => onDecision(finding.id, 'duplicate')}><Check size={16} /> <span className="ml-1">Это дубль</span></Button>
          <Button disabled={busy || finding.status === 'dismissed'} variant="outline" onClick={() => onDecision(finding.id, 'different')}><Users size={16} /> <span className="ml-1">Разные люди</span></Button>
          <Button disabled={busy || finding.status === 'ignored'} variant="ghost" onClick={() => onDecision(finding.id, 'ignored')}>Игнорировать</Button>
        </div>
      </div>
      <div className="mt-5 grid grid-cols-2 gap-2 lg:grid-cols-5">
        {summary.map(([label, value]) => (
          <div key={label} className="rounded-xl border border-[rgb(var(--border))] px-3 py-2">
            <div className="text-[11px] text-neutral-500">{label}</div>
            <div className="mt-1 truncate text-sm font-semibold" title={value}>{value}</div>
          </div>
        ))}
      </div>
      <div className="mt-4 grid grid-cols-1 gap-4 2xl:grid-cols-2">
        {accounts.map((account) => <AccountPanel key={account.userId} account={account} data={data} onVerify={onVerify} busy={busy} />)}
      </div>
      <div className="mt-5 border-t border-[rgb(var(--border))] pt-5">
        <div className="mb-3 flex items-center gap-2 font-semibold"><Sparkles size={17} /> Почему система связала аккаунты</div>
        <EvidenceList evidence={data.evidence || []} />
      </div>
    </Card>
  );
}

function SuspiciousFinding({ finding, onVerify, onIgnore, busy }) {
  const data = finding.data || {};
  const account = data.account || {};
  return (
    <Card>
      <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <Badge intent={scoreIntent(finding.score)}>{finding.score}%</Badge>
            <div className="text-lg font-semibold">{account.displayName || account.login || 'Странный аккаунт'}</div>
            <Badge intent="outline">{statusLabel(finding.status)}</Badge>
          </div>
          <div className="mt-2 text-sm text-neutral-500">{account.login || 'без логина'} · {account.email || 'без email'} · создан {formatDate(account.createdAt)}</div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Link to={`/admin/users/${account.userId}`}><Button variant="outline"><ExternalLink size={16} /> <span className="ml-1">Открыть</span></Button></Link>
          <Button disabled={busy} onClick={() => onVerify(account.userId)}><ShieldCheck size={16} /> <span className="ml-1">Проверен</span></Button>
          <Button disabled={busy} variant="ghost" onClick={() => onIgnore(account.userId)}>Игнорировать</Button>
        </div>
      </div>
      <div className="mt-5"><EvidenceList evidence={data.evidence || []} /></div>
    </Card>
  );
}

function VerifiedAccounts({ rows, onReset, busy }) {
  const verified = (rows || []).filter((x) => x.subjectType === 'account' && x.decision === 'verified');
  if (!verified.length) return <Card><div className="text-sm text-neutral-500">Пока нет аккаунтов, помеченных проверенными.</div></Card>;
  return (
    <div className="space-y-3">
      {verified.map((row) => {
        const account = row.data || {};
        return (
          <Card key={row.id}>
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="min-w-0">
              <div className="flex items-center gap-2 font-semibold"><ShieldCheck size={17} /> {account.displayName || account.login || 'Проверенный аккаунт'}</div>
              <div className="mt-1 break-all text-xs text-neutral-500">{account.login || account.email || row.userId} · проверен {formatDate(row.updatedAtUtc)}</div>
              <div className="mt-1 text-xs text-neutral-500">Создан: {formatDate(account.createdAt)} · последняя активность: {formatDate(account.lastActivityAt)}</div>
              {row.note ? <div className="mt-2 text-sm">{row.note}</div> : null}
            </div>
            <div className="flex gap-2">
              <Link to={`/admin/users/${row.userId}`}><Button variant="outline">Открыть</Button></Link>
              <Button disabled={busy} variant="ghost" onClick={() => onReset(row.userId)}>Снять отметку</Button>
            </div>
          </div>
          </Card>
        );
      })}
    </div>
  );
}

export default function AdminAiAccountManagerPage() {
  const notify = useNotify();
  const [run, setRun] = useState(null);
  const [findings, setFindings] = useState([]);
  const [reviews, setReviews] = useState([]);
  const [tab, setTab] = useState('duplicates');
  const [query, setQuery] = useState('');
  const [minScore, setMinScore] = useState('42');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [pageError, setPageError] = useState(null);

  const loadRun = useCallback(async () => {
    const latest = await getLatestAccountAnalysisRun();
    setRun(latest);
    return latest;
  }, []);

  const loadFindings = useCallback(async (selectedRun) => {
    if (!selectedRun?.id || selectedRun.status !== 'completed') {
      setFindings([]);
      return;
    }
    const result = await getAccountAnalysisFindings({ runId: selectedRun.id, minScore: Number(minScore) || 0, pageSize: 200 });
    setFindings(Array.isArray(result?.items) ? result.items : []);
  }, [minScore]);

  const loadReviews = useCallback(async () => {
    const rows = await getAccountReviews({ take: 2000 });
    setReviews(Array.isArray(rows) ? rows : []);
  }, []);

  const loadAll = useCallback(async () => {
    try {
      setLoading(true);
      const latest = await loadRun();
      await Promise.all([loadFindings(latest), loadReviews()]);
      setPageError(null);
    } catch (error) {
      setPageError(handleApiError(error, notify, 'Не удалось загрузить менеджер аккаунтов'));
    } finally {
      setLoading(false);
    }
  }, [loadFindings, loadReviews, loadRun, notify]);

  useEffect(() => { loadAll(); }, [loadAll]);

  useEffect(() => {
    if (!run?.id || !ACTIVE_RUN_STATUSES.has(run.status)) return undefined;
    const timer = window.setInterval(async () => {
      try {
        const fresh = await getAccountAnalysisRun(run.id);
        setRun(fresh);
        if (!ACTIVE_RUN_STATUSES.has(fresh.status)) {
          window.clearInterval(timer);
          await Promise.all([loadFindings(fresh), loadReviews()]);
        }
      } catch (error) {
        window.clearInterval(timer);
        setPageError(handleApiError(error, notify, 'Не удалось обновить статус анализа'));
      }
    }, 2000);
    return () => window.clearInterval(timer);
  }, [loadFindings, loadReviews, notify, run?.id, run?.status]);

  const startScan = async () => {
    try {
      setBusy(true);
      const created = await startAccountAnalysis();
      setRun(created);
      setFindings([]);
      notify.success('Полный анализ аккаунтов запущен');
      setPageError(null);
    } catch (error) {
      const parsed = handleApiError(error, notify, 'Не удалось запустить анализ');
      setPageError(parsed);
      await loadRun().catch(() => null);
    } finally {
      setBusy(false);
    }
  };

  const decision = async (findingId, value) => {
    try {
      setBusy(true);
      await decideAccountFinding(findingId, value);
      notify.success(value === 'duplicate' ? 'Дубль подтверждён' : value === 'different' ? 'Пара помечена как разные люди' : 'Пара скрыта');
      await Promise.all([loadFindings(run), loadReviews()]);
    } catch (error) {
      setPageError(handleApiError(error, notify, 'Не удалось сохранить решение'));
    } finally {
      setBusy(false);
    }
  };

  const accountDecision = async (userId, value) => {
    try {
      setBusy(true);
      await decideAccount(userId, value);
      notify.success(value === 'verified' ? 'Аккаунт помечен проверенным и останется уликой для новых твинков' : value === 'unverified' ? 'Отметка снята' : 'Аккаунт скрыт');
      await Promise.all([loadFindings(run), loadReviews()]);
    } catch (error) {
      setPageError(handleApiError(error, notify, 'Не удалось сохранить отметку аккаунта'));
    } finally {
      setBusy(false);
    }
  };

  const verifiedIds = useMemo(() => new Set(
    reviews
      .filter((row) => row.subjectType === 'account' && row.decision === 'verified' && row.userId)
      .map((row) => String(row.userId)),
  ), [reviews]);

  const filtered = useMemo(() => {
    const search = query.trim().toLowerCase();
    return findings.filter((item) => {
      const accountIds = item.kind === 'duplicate'
        ? (item.data?.accounts || []).map((account) => String(account.userId))
        : [String(item.data?.account?.userId || item.primaryUserId || '')];
      const allAccountsVerified = accountIds.length > 0 && accountIds.every((userId) => verifiedIds.has(userId));
      const suspiciousAccountVerified = item.kind === 'suspicious' && accountIds.some((userId) => verifiedIds.has(userId));
      if (tab === 'duplicates' && (item.kind !== 'duplicate' || item.status !== 'open' || allAccountsVerified)) return false;
      if (tab === 'suspicious' && (item.kind !== 'suspicious' || item.status !== 'open' || suspiciousAccountVerified)) return false;
      if (tab === 'reviewed' && item.status === 'open') return false;
      if (!search) return true;
      return JSON.stringify(item.data || {}).toLowerCase().includes(search) || String(item.findingKey || '').toLowerCase().includes(search);
    });
  }, [findings, query, tab, verifiedIds]);

  const openDuplicates = findings.filter((item) => {
    if (item.kind !== 'duplicate' || item.status !== 'open') return false;
    const ids = (item.data?.accounts || []).map((account) => String(account.userId));
    return !(ids.length > 0 && ids.every((userId) => verifiedIds.has(userId)));
  }).length;
  const openSuspicious = findings.filter((item) => item.kind === 'suspicious' && item.status === 'open' && !verifiedIds.has(String(item.data?.account?.userId || item.primaryUserId || ''))).length;
  const reviewedCount = findings.filter((x) => x.status !== 'open').length;
  const verifiedCount = reviews.filter((x) => x.subjectType === 'account' && x.decision === 'verified').length;
  const activeRun = run && ACTIVE_RUN_STATUSES.has(run.status);

  return (
    <div className="space-y-5 sm:space-y-6">
      <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
        <div className="min-w-0">
          <div className="mb-2 flex flex-wrap items-center gap-2 text-xs text-neutral-500">
            <span>ИИ</span><span>›</span><span>Менеджер аккаунтов</span>
          </div>
          <h1 className="flex items-center gap-3 text-2xl font-semibold sm:text-3xl">
            <span className="flex h-11 w-11 items-center justify-center rounded-2xl border border-[rgb(var(--border))]"><BrainCircuit size={23} /></span>
            Менеджер аккаунтов
          </h1>
          <p className="mt-2 max-w-4xl text-sm text-neutral-500">
            Ищет твинков, транслитерации, опечатки, тестовые профили и технические совпадения. Разные устройства никогда не уменьшают вероятность; одинаковое устройство только добавляет улику.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" disabled={loading || busy} onClick={loadAll}><RefreshCcw size={16} /> <span className="ml-1">Обновить</span></Button>
          <Button disabled={busy || activeRun} onClick={startScan}>
            {activeRun ? <Loader2 size={16} className="animate-spin" /> : <ScanSearch size={16} />}
            <span className="ml-1">{activeRun ? 'Анализ идёт' : 'Проанализировать аккаунты'}</span>
          </Button>
        </div>
      </div>

      {pageError ? <AppErrorPanel error={pageError} title="Проблема менеджера аккаунтов" /> : null}
      <RunProgress run={run} />

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Metric label="Возможные дубли" value={run?.duplicateFindings ?? 0} hint={`${openDuplicates} требуют решения`} icon={Users} />
        <Metric label="Странные аккаунты" value={run?.suspiciousFindings ?? 0} hint={`${openSuspicious} требуют проверки`} icon={Fingerprint} />
        <Metric label="Проверено решений" value={reviewedCount} hint="Дубли, разные люди и игнор" icon={CheckCircle2} />
        <Metric label="Проверенные якоря" value={verifiedCount} hint="Не подозреваются сами, но ловят новые твинки" icon={ShieldCheck} />
      </div>

      <Card>
        <div className="flex flex-col gap-3 xl:flex-row xl:items-end xl:justify-between">
          <div className="flex flex-wrap gap-2">
            {[
              ['duplicates', `Дубли ${openDuplicates}`],
              ['suspicious', `Странные ${openSuspicious}`],
              ['reviewed', `Проверенные пары ${reviewedCount}`],
              ['verified', `Якоря ${verifiedCount}`],
            ].map(([key, label]) => (
              <Button key={key} variant={tab === key ? 'default' : 'outline'} onClick={() => setTab(key)}>{label}</Button>
            ))}
          </div>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-[minmax(240px,1fr),160px] xl:w-[560px]">
            <Field label="Поиск"><Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="имя, логин, email, ID" /></Field>
            <Field label="Минимальный балл"><Select value={minScore} onChange={(event) => setMinScore(event.target.value)}><option value="0">Все</option><option value="42">От 42</option><option value="60">От 60</option><option value="75">От 75</option><option value="90">От 90</option></Select></Field>
          </div>
        </div>
      </Card>

      {loading ? <Card><div className="flex items-center gap-2 text-neutral-500"><Loader2 size={18} className="animate-spin" /> Загружаю результаты…</div></Card> : null}
      {!loading && !run ? <Card><div className="py-8 text-center text-neutral-500"><Bot size={28} className="mx-auto mb-3" />Нажми «Проанализировать аккаунты», чтобы получить первый полный отчёт.</div></Card> : null}

      {!loading && tab === 'verified' ? <VerifiedAccounts rows={reviews} busy={busy} onReset={(userId) => accountDecision(userId, 'unverified')} /> : null}

      {!loading && tab !== 'verified' ? (
        <div className="space-y-4">
          {filtered.map((finding) => finding.kind === 'duplicate'
            ? <DuplicateFinding key={finding.id} finding={finding} busy={busy} verifiedIds={verifiedIds} onDecision={decision} onVerify={(userId) => accountDecision(userId, 'verified')} />
            : <SuspiciousFinding key={finding.id} finding={finding} busy={busy} onVerify={(userId) => accountDecision(userId, 'verified')} onIgnore={(userId) => accountDecision(userId, 'ignored')} />)}
          {run?.status === 'completed' && filtered.length === 0 ? (
            <Card><div className="py-8 text-center text-neutral-500"><CheckCircle2 size={28} className="mx-auto mb-3" />В этой вкладке сейчас ничего нет.</div></Card>
          ) : null}
        </div>
      ) : null}

      <Card className="border-dashed">
        <div className="flex gap-3">
          <History size={19} className="mt-0.5 shrink-0" />
          <div className="text-sm text-neutral-500">
            Проверенный аккаунт исчезает из списка странных аккаунтов, но остаётся эталонным якорем. Когда появится новый похожий профиль, система покажет именно новый аккаунт и приложит проверенный как улику.
          </div>
        </div>
      </Card>
    </div>
  );
}
