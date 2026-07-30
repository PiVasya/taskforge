import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import {
  Archive,
  Ban,
  Bot,
  BrainCircuit,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  CircleStop,
  ExternalLink,
  Fingerprint,
  History,
  Loader2,
  Merge,
  Pencil,
  Play,
  RefreshCcw,
  RotateCcw,
  ScanSearch,
  ShieldCheck,
  ShieldOff,
  Sparkles,
  Trash2,
  Unlock,
  UserCheck,
  Users,
  X,
} from 'lucide-react';
import { Badge, Button, Card, Field, Input, Select } from '../../components/ui';
import AppErrorPanel from '../../components/AppErrorPanel';
import { useNotify } from '../../components/notify/NotifyProvider';
import { handleApiError } from '../../utils/handleApiError';
import {
  archiveAccountOperation,
  blockAccount,
  cancelAccountOperation,
  createAccountOperation,
  decideAccount,
  decideAccountFinding,
  deleteAccountDecision,
  deleteAccountFindingDecision,
  deleteAccountReview,
  getAccountAnalysisFindings,
  getAccountAnalysisRun,
  getAccountOperations,
  getAccountReviews,
  getBlockedAccounts,
  getLatestAccountAnalysisRun,
  retryAccountOperation,
  restoreAccountOperation,
  startAccountAnalysis,
  unblockAccount,
  updateAccountOperation,
  updateAccountReview,
} from '../../api/accountIntelligence';

const ACTIVE_RUN_STATUSES = new Set(['queued', 'starting', 'running']);
const ACTIVE_OPERATION_STATUSES = new Set(['queued', 'starting', 'running']);

const toLocalDateTimeInput = (value) => {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return local.toISOString().slice(0, 16);
};

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
  'merge-queued': 'Объединение поставлено в очередь',
  'resolved-by-merge': 'Разрешено объединением',
  'resolved-by-delete': 'Разрешено удалением',
}[status] || status || '—');

const operationLabel = (type) => ({
  merge: 'Объединение аккаунтов',
  delete: 'Удаление аккаунта',
  block: 'Блокировка аккаунта',
  unblock: 'Разблокировка аккаунта',
}[type] || type || 'Операция');

const operationIntent = (status) => ({
  completed: 'success',
  failed: 'danger',
  cancelled: 'outline',
  running: 'warning',
  starting: 'warning',
  queued: 'outline',
}[status] || 'neutral');

const accountTitle = (account) => account?.displayName || account?.login || account?.email || account?.userId || 'Пользователь';
const accountConfirmation = (account) => account?.login || account?.displayName || account?.userId || '';

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

function Modal({ title, children, onClose, busy, width = 'max-w-3xl' }) {
  return (
    <div className="fixed inset-0 z-[120] flex items-center justify-center bg-black/65 p-3 sm:p-6" onMouseDown={(event) => { if (event.target === event.currentTarget && !busy) onClose(); }}>
      <div className={`max-h-[92vh] w-full ${width} overflow-y-auto rounded-3xl border border-[rgb(var(--border))] bg-[rgb(var(--card))] shadow-2xl`}>
        <div className="sticky top-0 z-10 flex items-center justify-between gap-3 border-b border-[rgb(var(--border))] bg-[rgb(var(--card))] px-5 py-4">
          <div className="text-lg font-semibold">{title}</div>
          <Button variant="ghost" disabled={busy} onClick={onClose}><X size={18} /></Button>
        </div>
        <div className="p-5">{children}</div>
      </div>
    </div>
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

function AccountBadges({ account, data, blocked }) {
  const badges = [];
  if (account.verified) badges.push(['Проверенный', 'success']);
  if (blocked || account.blocked) badges.push(['Заблокирован', 'danger']);
  if (String(data?.olderUserId) === String(account.userId)) badges.push(['Старый аккаунт', 'outline']);
  if (String(data?.newerUserId) === String(account.userId)) badges.push(['Новый аккаунт', 'outline']);
  if (String(data?.currentlyActiveUserId) === String(account.userId)) badges.push(['Активнее сейчас', 'warning']);
  if (String(data?.historicallyRicherUserId) === String(account.userId)) badges.push(['Больше истории', 'outline']);
  if (String(data?.suggestedPrimaryUserId) === String(account.userId)) badges.push(['Рекомендуемый основной', 'success']);
  if (String(data?.verifiedAnchorUserId) === String(account.userId)) badges.push(['Проверенный якорь', 'success']);
  if (String(data?.suspectUserId) === String(account.userId)) badges.push(['Новый подозреваемый', 'danger']);
  return <div className="flex flex-wrap gap-1.5">{badges.map(([label, intent]) => <Badge key={label} intent={intent}>{label}</Badge>)}</div>;
}

function AccountPanel({ account, data, verified, blocked, onVerify, onUnverify, onBlock, onUnblock, onDelete, busy }) {
  const groupText = (account.groups || []).map((x) => x.name || x.code).filter(Boolean).join(', ');
  const minecraftText = (account.minecraft || []).map((x) => x.playerName).filter(Boolean).join(', ');
  const view = { ...account, verified: account.verified || verified, blocked: account.blocked || blocked };
  return (
    <div className="min-w-0 rounded-2xl border border-[rgb(var(--border))] bg-[rgba(var(--card)/0.72)] p-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0">
          <div className="truncate text-lg font-semibold">{accountTitle(view)}</div>
          <div className="mt-1 break-all text-xs text-neutral-500">{view.login || 'без логина'} · {view.email || 'без email'}</div>
        </div>
        <Link className="inline-flex items-center gap-1 text-sm text-[rgb(var(--accent-600))]" to={`/admin/users/${view.userId}`}>
          Открыть <ExternalLink size={14} />
        </Link>
      </div>
      <div className="mt-3"><AccountBadges account={view} data={data} blocked={blocked} /></div>
      <div className="mt-4 grid grid-cols-2 gap-3 text-sm lg:grid-cols-4">
        <div><div className="text-xs text-neutral-500">Создан</div><div className="mt-1">{formatDate(view.createdAt)}</div></div>
        <div><div className="text-xs text-neutral-500">Последняя активность</div><div className="mt-1">{formatDate(view.lastActivityAt)}</div></div>
        <div><div className="text-xs text-neutral-500">Активность сейчас</div><div className="mt-1 font-semibold">{view.activityScore ?? 0}/100</div></div>
        <div><div className="text-xs text-neutral-500">Историческая ценность</div><div className="mt-1 font-semibold">{view.historicalValueScore ?? 0}/100</div></div>
      </div>
      <div className="mt-4 space-y-1 text-xs text-neutral-500">
        <div>Действия: {view.totalMeaningfulActions ?? 0} · решено: {view.solvedCount ?? 0} · рейтинг: {view.totalScore ?? 0}</div>
        <div>Группы: {groupText || 'нет'}</div>
        <div>Minecraft: {minecraftText || 'нет'}</div>
        <div>Телефон: {view.phoneNumber || 'не указан'} · Telegram: {view.telegramUsername || 'нет'}</div>
      </div>
      <div className="mt-4 flex flex-wrap gap-2">
        {view.verified ? (
          <Button variant="outline" disabled={busy} onClick={() => onUnverify(view.userId)}><ShieldOff size={16} /><span className="ml-1">Снять «проверен»</span></Button>
        ) : (
          <Button variant="outline" disabled={busy} onClick={() => onVerify(view.userId)}><UserCheck size={16} /><span className="ml-1">Пометить проверенным</span></Button>
        )}
        {view.blocked ? (
          <Button variant="outline" disabled={busy} onClick={() => onUnblock(view)}><Unlock size={16} /><span className="ml-1">Разблокировать</span></Button>
        ) : (
          <Button variant="outline" disabled={busy} onClick={() => onBlock(view)}><Ban size={16} /><span className="ml-1">Заблокировать</span></Button>
        )}
        <Button variant="ghost" disabled={busy} onClick={() => onDelete(view)}><Trash2 size={16} /><span className="ml-1">Удалить</span></Button>
      </div>
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

function DuplicateFinding({ finding, verifiedIds, blockedIds, handlers, busy }) {
  const rawData = finding.data || {};
  const accounts = (rawData.accounts || []).map((account) => ({
    ...account,
    verified: account.verified || verifiedIds.has(String(account.userId)),
    blocked: account.blocked || blockedIds.has(String(account.userId)),
  }));
  const data = { ...rawData, accounts };
  const accountName = (userId) => accountTitle(accounts.find((item) => String(item.userId) === String(userId)));
  const summary = [
    ['Старый аккаунт', accountName(data.olderUserId)],
    ['Новый аккаунт', accountName(data.newerUserId)],
    ['Активнее сейчас', accountName(data.currentlyActiveUserId)],
    ['Больше полезной истории', accountName(data.historicallyRicherUserId)],
    ['Рекомендуемый основной', accountName(data.suggestedPrimaryUserId)],
  ];
  const reviewed = finding.status !== 'open';
  return (
    <Card>
      <div className="flex flex-col gap-3 xl:flex-row xl:items-start xl:justify-between">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <Badge intent={scoreIntent(finding.score)}>{finding.score}%</Badge>
            <div className="text-lg font-semibold">Возможный дубль</div>
            <Badge intent={reviewed ? 'success' : 'outline'}>{statusLabel(finding.status)}</Badge>
            <Badge intent="outline">вероятность {Math.round((finding.modelProbability || 0) * 100)}%</Badge>
          </div>
          <div className="mt-2 text-sm text-neutral-500">Разница регистрации: {data.registrationGapDays ?? '—'} дн.</div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button disabled={busy} onClick={() => handlers.openMerge(finding, accounts)}><Merge size={16} /><span className="ml-1">Объединить</span></Button>
          <Button disabled={busy} variant="outline" onClick={() => handlers.decision(finding.id, 'duplicate')}><CheckCircle2 size={16} /><span className="ml-1">Это дубль</span></Button>
          <Button disabled={busy} variant="outline" onClick={() => handlers.decision(finding.id, 'different')}><Users size={16} /><span className="ml-1">Разные люди</span></Button>
          <Button disabled={busy} variant="ghost" onClick={() => handlers.decision(finding.id, 'ignored')}>Игнорировать</Button>
          {reviewed ? <Button disabled={busy} variant="ghost" onClick={() => handlers.resetFinding(finding.id)}><RotateCcw size={16} /><span className="ml-1">Снять решение</span></Button> : null}
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
        {accounts.map((account) => (
          <AccountPanel
            key={account.userId}
            account={account}
            data={data}
            verified={verifiedIds.has(String(account.userId))}
            blocked={blockedIds.has(String(account.userId))}
            onVerify={handlers.verify}
            onUnverify={handlers.unverify}
            onBlock={handlers.openBlock}
            onUnblock={handlers.unblock}
            onDelete={handlers.openDelete}
            busy={busy}
          />
        ))}
      </div>
      <div className="mt-5 border-t border-[rgb(var(--border))] pt-5">
        <div className="mb-3 flex items-center gap-2 font-semibold"><Sparkles size={17} /> Почему система связала аккаунты</div>
        <EvidenceList evidence={data.evidence || []} />
      </div>
    </Card>
  );
}

function SuspiciousFinding({ finding, verifiedIds, blockedIds, handlers, busy }) {
  const data = finding.data || {};
  const account = data.account || {};
  return (
    <Card>
      <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <Badge intent={scoreIntent(finding.score)}>{finding.score}%</Badge>
            <div className="text-lg font-semibold">{accountTitle(account)}</div>
            <Badge intent="outline">{statusLabel(finding.status)}</Badge>
          </div>
          <div className="mt-2 text-sm text-neutral-500">{account.login || 'без логина'} · {account.email || 'без email'} · создан {formatDate(account.createdAt)}</div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Link to={`/admin/users/${account.userId}`}><Button variant="outline"><ExternalLink size={16} /><span className="ml-1">Открыть</span></Button></Link>
          <Button disabled={busy} onClick={() => handlers.verify(account.userId)}><ShieldCheck size={16} /><span className="ml-1">Проверен</span></Button>
          <Button disabled={busy} variant="outline" onClick={() => handlers.openBlock(account)}><Ban size={16} /><span className="ml-1">Блокировать</span></Button>
          <Button disabled={busy} variant="ghost" onClick={() => handlers.openDelete(account)}><Trash2 size={16} /><span className="ml-1">Удалить</span></Button>
          <Button disabled={busy} variant="ghost" onClick={() => handlers.decision(finding.id, 'ignored')}>Игнорировать</Button>
          {finding.status !== 'open' ? <Button disabled={busy} variant="ghost" onClick={() => handlers.resetFinding(finding.id)}>Снять решение</Button> : null}
        </div>
      </div>
      <div className="mt-5"><EvidenceList evidence={data.evidence || []} /></div>
    </Card>
  );
}

function ReviewRegistry({ rows, query, onEdit, onDelete, busy }) {
  const search = String(query || '').trim().toLowerCase();
  const visible = (rows || []).filter((row) => {
    if (row.subjectType === 'account' && row.decision === 'verified') return false;
    if (!search) return true;
    return JSON.stringify(row).toLowerCase().includes(search);
  });
  const decisionText = (value) => ({ duplicate: 'Дубль', different: 'Разные люди', ignored: 'Игнорировать', unverified: 'Не проверен', verified: 'Проверен' }[value] || value || '—');
  if (!visible.length) return <Card><div className="text-sm text-neutral-500">Сохранённых решений по текущему фильтру нет.</div></Card>;
  return (
    <div className="space-y-3">
      {visible.map((row) => {
        const data = row.data || {};
        const accounts = Array.isArray(data.accounts) ? data.accounts : data.account ? [data.account] : [];
        const title = row.subjectType === 'pair'
          ? accounts.length ? accounts.map(accountTitle).join(' ↔ ') : `${row.userId || 'Аккаунт'} ↔ ${row.otherUserId || 'Аккаунт'}`
          : accountTitle(accounts[0] || { userId: row.userId });
        return (
          <Card key={row.id}>
            <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2"><History size={17} /><span className="font-semibold">{title}</span><Badge intent={row.decision === 'duplicate' ? 'danger' : row.decision === 'different' ? 'success' : 'outline'}>{decisionText(row.decision)}</Badge></div>
                <div className="mt-1 break-all text-xs text-neutral-500">{row.subjectType === 'pair' ? 'Решение по паре' : 'Решение по аккаунту'} · {formatDate(row.updatedAtUtc)}</div>
                {row.note ? <div className="mt-2 text-sm">{row.note}</div> : null}
              </div>
              <div className="flex flex-wrap gap-2">
                <Button disabled={busy} variant="outline" onClick={() => onEdit(row)}><Pencil size={16} /><span className="ml-1">Изменить</span></Button>
                <Button disabled={busy} variant="ghost" onClick={() => onDelete(row)}><RotateCcw size={16} /><span className="ml-1">Снять решение</span></Button>
              </div>
            </div>
          </Card>
        );
      })}
    </div>
  );
}

function VerifiedAccounts({ rows, onReset, onEdit, busy }) {
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
                <div className="flex items-center gap-2 font-semibold"><ShieldCheck size={17} /> {accountTitle(account)}</div>
                <div className="mt-1 break-all text-xs text-neutral-500">{account.login || account.email || row.userId} · проверен {formatDate(row.updatedAtUtc)}</div>
                {row.note ? <div className="mt-2 text-sm">{row.note}</div> : null}
              </div>
              <div className="flex flex-wrap gap-2">
                <Link to={`/admin/users/${row.userId}`}><Button variant="outline">Открыть</Button></Link>
                <Button disabled={busy} variant="outline" onClick={() => onEdit(row)}><Pencil size={16} /><span className="ml-1">Изменить</span></Button>
                <Button disabled={busy} variant="ghost" onClick={() => onReset(row.userId)}><ShieldOff size={16} /><span className="ml-1">Снять отметку</span></Button>
              </div>
            </div>
          </Card>
        );
      })}
    </div>
  );
}

function BlockedAccounts({ rows, handlers, busy }) {
  if (!rows.length) return <Card><div className="text-sm text-neutral-500">Заблокированных аккаунтов нет.</div></Card>;
  return (
    <div className="space-y-3">
      {rows.map((row) => {
        const account = row.user || row;
        return (
          <Card key={account.userId}>
            <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2"><Ban size={18} /><span className="text-lg font-semibold">{accountTitle(account)}</span><Badge intent="danger">Заблокирован</Badge></div>
                <div className="mt-1 break-all text-xs text-neutral-500">{account.login || 'без логина'} · {account.email || 'без email'} · {account.userId}</div>
                <div className="mt-3 text-sm"><span className="text-neutral-500">Причина:</span> {row.reason || account.blockReason || 'manual'}</div>
                {(row.note || account.blockNote) ? <div className="mt-1 text-sm"><span className="text-neutral-500">Комментарий:</span> {row.note || account.blockNote}</div> : null}
                <div className="mt-2 text-xs text-neutral-500">Заблокирован: {formatDate(row.blockedAtUtc || account.blockedAtUtc)} · обновлён: {formatDate(row.updatedAtUtc || account.updatedAtUtc)} · до: {formatDate(row.expiresAtUtc || account.expiresAtUtc)}</div>
              </div>
              <div className="flex flex-wrap gap-2">
                <Link to={`/admin/users/${account.userId}`}><Button variant="outline">Открыть</Button></Link>
                <Button disabled={busy} variant="outline" onClick={() => handlers.openBlock(account, row)}><Pencil size={16} /><span className="ml-1">Изменить</span></Button>
                <Button disabled={busy} onClick={() => handlers.unblock(account)}><Unlock size={16} /><span className="ml-1">Разблокировать</span></Button>
                <Button disabled={busy} variant="ghost" onClick={() => handlers.openDelete(account)}><Trash2 size={16} /><span className="ml-1">Удалить</span></Button>
              </div>
            </div>
          </Card>
        );
      })}
    </div>
  );
}

function OperationsList({ rows, handlers, busy, archived }) {
  if (!rows.length) return <Card><div className="text-sm text-neutral-500">Операций управления аккаунтами пока нет.</div></Card>;
  return (
    <div className="space-y-3">
      {rows.map((operation) => {
        const active = ACTIVE_OPERATION_STATUSES.has(operation.status);
        const steps = Array.isArray(operation.steps) ? operation.steps : [];
        return (
          <Card key={operation.id}>
            <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  {active ? <Loader2 size={18} className="animate-spin" /> : operation.status === 'completed' ? <CheckCircle2 size={18} /> : <CircleStop size={18} />}
                  <span className="text-lg font-semibold">{operationLabel(operation.type)}</span>
                  <Badge intent={operationIntent(operation.status)}>{operation.status}</Badge>
                  <span className="text-xs text-neutral-500">попытка {operation.attemptCount || 0}</span>
                </div>
                <div className="mt-2 text-sm text-neutral-500">
                  {accountTitle(operation.source)}{operation.target ? ` → ${accountTitle(operation.target)}` : ''} · {formatDate(operation.createdAtUtc)}
                </div>
                {operation.reason ? <div className="mt-2 text-sm">{operation.reason}</div> : null}
                <div className="mt-3 h-2 overflow-hidden rounded-full bg-[rgba(var(--border)/0.7)]"><div className="h-full bg-[rgb(var(--accent-600))]" style={{ width: `${Math.max(2, operation.progressPercent || 0)}%` }} /></div>
                {operation.error?.message ? <div className="mt-3 rounded-xl border border-red-500/30 bg-red-500/5 p-3 text-sm text-red-500">{operation.error.message}{operation.error.detail ? `: ${operation.error.detail}` : ''}</div> : null}
                {steps.length ? (
                  <details className="mt-3">
                    <summary className="cursor-pointer text-sm font-medium">Шаги операции ({steps.filter((x) => x.status === 'completed').length}/{steps.length})</summary>
                    <div className="mt-2 space-y-1">
                      {steps.map((step) => <div key={step.key} className="flex items-center justify-between gap-3 rounded-lg border border-[rgb(var(--border))] px-3 py-2 text-xs"><span>{step.title}</span><Badge intent={operationIntent(step.status)}>{step.status}</Badge></div>)}
                    </div>
                  </details>
                ) : null}
              </div>
              <div className="flex flex-wrap gap-2">
                {operation.status === 'queued' ? <Button disabled={busy} variant="outline" onClick={() => handlers.cancel(operation.id)}><CircleStop size={16} /><span className="ml-1">Отменить</span></Button> : null}
                {operation.status === 'failed' || operation.status === 'cancelled' ? <Button disabled={busy} onClick={() => handlers.retry(operation.id)}><Play size={16} /><span className="ml-1">Повторить</span></Button> : null}
                {operation.status === 'queued' || operation.status === 'failed' || operation.status === 'cancelled' ? <Button disabled={busy} variant="outline" onClick={() => handlers.edit(operation)}><Pencil size={16} /><span className="ml-1">Изменить</span></Button> : null}
                {!active && !archived ? <Button disabled={busy} variant="ghost" onClick={() => handlers.archive(operation.id)}><Archive size={16} /><span className="ml-1">Архивировать</span></Button> : null}
                {archived ? <Button disabled={busy} variant="outline" onClick={() => handlers.restore(operation.id)}><RotateCcw size={16} /><span className="ml-1">Восстановить</span></Button> : null}
              </div>
            </div>
          </Card>
        );
      })}
    </div>
  );
}

function AccountActionModal({ dialog, setDialog, onSubmit, busy }) {
  if (!dialog) return null;
  const mode = dialog.mode;
  const accounts = dialog.accounts || [];
  const primaryId = dialog.primaryUserId || dialog.suggestedPrimaryUserId || accounts[0]?.userId;
  const primary = accounts.find((x) => String(x.userId) === String(primaryId));
  const source = mode === 'merge' ? accounts.find((x) => String(x.userId) !== String(primaryId)) : dialog.account;
  const expected = accountConfirmation(source);

  const patch = (value) => setDialog((prev) => ({ ...prev, ...value }));
  return (
    <Modal
      title={mode === 'merge' ? 'Объединить аккаунты' : mode === 'delete' ? 'Удалить аккаунт' : mode === 'block' ? (dialog.editing ? 'Изменить блокировку' : 'Заблокировать аккаунт') : mode === 'operation' ? 'Изменить операцию' : 'Изменить решение'}
      onClose={() => setDialog(null)}
      busy={busy}
    >
      {mode === 'merge' ? (
        <div className="space-y-5">
          <div className="rounded-2xl border border-amber-500/30 bg-amber-500/5 p-4 text-sm">
            Данные дубля будут перенесены во всех сервисах. Исходный аккаунт сразу блокируется, а после успешного переноса становится недоступным. Операцию можно повторить после сбоя без двойного переноса.
          </div>
          <Field label="Какой аккаунт оставить основным">
            <div className="space-y-2">
              {accounts.map((account) => (
                <label key={account.userId} className={`flex cursor-pointer items-start gap-3 rounded-2xl border p-4 ${String(primaryId) === String(account.userId) ? 'border-[rgb(var(--accent-600))] bg-[rgba(var(--accent-500)/0.08)]' : 'border-[rgb(var(--border))]'}`}>
                  <input type="radio" name="primaryAccount" checked={String(primaryId) === String(account.userId)} onChange={() => patch({ primaryUserId: account.userId, confirmation: '' })} />
                  <div><div className="font-semibold">{accountTitle(account)}</div><div className="mt-1 text-xs text-neutral-500">{account.login || account.userId} · действий {account.totalMeaningfulActions || 0} · решено {account.solvedCount || 0} · рейтинг {account.totalScore || 0}</div></div>
                </label>
              ))}
            </div>
          </Field>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2"><Card><div className="text-xs text-neutral-500">Останется</div><div className="mt-1 font-semibold">{accountTitle(primary)}</div></Card><Card><div className="text-xs text-neutral-500">Будет объединён и закрыт</div><div className="mt-1 font-semibold">{accountTitle(source)}</div></Card></div>
          <Field label="Причина / комментарий"><Input value={dialog.reason || ''} onChange={(event) => patch({ reason: event.target.value })} placeholder="Например: подтверждённый повторный аккаунт ученика" /></Field>
          <Field label={`Для подтверждения введи: ${expected}`}><Input value={dialog.confirmation || ''} onChange={(event) => patch({ confirmation: event.target.value })} /></Field>
        </div>
      ) : null}

      {mode === 'delete' ? (
        <div className="space-y-5">
          <div className="rounded-2xl border border-red-500/30 bg-red-500/5 p-4 text-sm text-red-600 dark:text-red-300">
            Будут удалены решения, попытки, прогресс, группы, интеграции, уведомления и связанные записи во всех сервисах. По умолчанию Identity оставит обезличенный технический след для аудита.
          </div>
          <Card><div className="font-semibold">{accountTitle(source)}</div><div className="mt-1 break-all text-xs text-neutral-500">{source?.login || 'без логина'} · {source?.email || 'без email'} · {source?.userId}</div></Card>
          <Field label="Причина удаления"><Input value={dialog.reason || ''} onChange={(event) => patch({ reason: event.target.value })} placeholder="Мусорный аккаунт / подтверждённый дубль" /></Field>
          <label className="flex items-start gap-3 rounded-2xl border border-[rgb(var(--border))] p-4"><input type="checkbox" checked={!!dialog.hardDelete} onChange={(event) => patch({ hardDelete: event.target.checked })} /><div><div className="font-medium">Полностью удалить запись Identity</div><div className="mt-1 text-xs text-neutral-500">Не рекомендуется. Без этой галочки персональные данные всё равно стираются, но остаётся технический tombstone с ID и историей операции.</div></div></label>
          <Field label={`Для подтверждения введи: ${expected}`}><Input value={dialog.confirmation || ''} onChange={(event) => patch({ confirmation: event.target.value })} /></Field>
        </div>
      ) : null}

      {mode === 'block' ? (
        <div className="space-y-4">
          <Card><div className="font-semibold">{accountTitle(source)}</div><div className="mt-1 break-all text-xs text-neutral-500">{source?.login || source?.userId}</div></Card>
          <Field label="Причина"><Input value={dialog.reason || ''} onChange={(event) => patch({ reason: event.target.value })} placeholder="Подозрительный дубль / нарушение / ручная проверка" /></Field>
          <Field label="Комментарий"><Input value={dialog.note || ''} onChange={(event) => patch({ note: event.target.value })} placeholder="Видно только администраторам" /></Field>
          <Field label="Заблокировать до (необязательно)"><Input type="datetime-local" value={dialog.expiresAtLocal || ''} onChange={(event) => patch({ expiresAtLocal: event.target.value })} /></Field>
        </div>
      ) : null}

      {mode === 'review' ? (
        <div className="space-y-4">
          <Field label="Решение"><Select value={dialog.decision || 'verified'} onChange={(event) => patch({ decision: event.target.value })}>{dialog.review?.subjectType === 'pair' ? <><option value="duplicate">Дубль</option><option value="different">Разные люди</option><option value="ignored">Игнорировать</option></> : <><option value="verified">Проверенный аккаунт</option><option value="ignored">Игнорировать</option><option value="unverified">Не проверен</option></>}</Select></Field>
          <Field label="Комментарий"><Input value={dialog.note || ''} onChange={(event) => patch({ note: event.target.value })} /></Field>
        </div>
      ) : null}

      {mode === 'operation' ? (
        <div className="space-y-4">
          <Card>
            <div className="font-semibold">{operationLabel(dialog.operation?.type)}</div>
            <div className="mt-1 text-xs text-neutral-500">{accountTitle(dialog.operation?.source)}{dialog.operation?.target ? ` → ${accountTitle(dialog.operation.target)}` : ''}</div>
          </Card>
          <Field label="Причина"><Input value={dialog.reason || ''} onChange={(event) => patch({ reason: event.target.value })} /></Field>
          <Field label="Комментарий"><Input value={dialog.note || ''} onChange={(event) => patch({ note: event.target.value })} /></Field>
          {dialog.operation?.type === 'block' ? <Field label="Заблокировать до (необязательно)"><Input type="datetime-local" value={dialog.expiresAtLocal || ''} onChange={(event) => patch({ expiresAtLocal: event.target.value })} /></Field> : null}
          {dialog.operation?.type === 'delete' ? <label className="flex items-start gap-3 rounded-2xl border border-[rgb(var(--border))] p-4"><input type="checkbox" checked={!!dialog.hardDelete} onChange={(event) => patch({ hardDelete: event.target.checked })} /><div><div className="font-medium">Полное удаление Identity-записи</div><div className="mt-1 text-xs text-neutral-500">Оставь выключенным для безопасного обезличенного tombstone.</div></div></label> : null}
        </div>
      ) : null}

      <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button variant="outline" disabled={busy} onClick={() => setDialog(null)}>Отмена</Button>
        <Button
          disabled={busy || ((mode === 'merge' || mode === 'delete') && String(dialog.confirmation || '').trim().toLowerCase() !== String(expected || '').trim().toLowerCase())}
          onClick={() => onSubmit({ ...dialog, primary, source })}
        >
          {busy ? <Loader2 size={16} className="animate-spin" /> : mode === 'merge' ? <Merge size={16} /> : mode === 'delete' ? <Trash2 size={16} /> : mode === 'block' ? <Ban size={16} /> : mode === 'operation' ? <Pencil size={16} /> : <ShieldCheck size={16} />}
          <span className="ml-1">{mode === 'merge' ? 'Запустить объединение' : mode === 'delete' ? 'Удалить аккаунт' : mode === 'block' ? 'Сохранить блокировку' : mode === 'operation' ? 'Сохранить операцию' : 'Сохранить решение'}</span>
        </Button>
      </div>
    </Modal>
  );
}

export default function AdminAiAccountManagerPage() {
  const notify = useNotify();
  const [searchParams, setSearchParams] = useSearchParams();
  const [run, setRun] = useState(null);
  const [findings, setFindings] = useState([]);
  const [reviews, setReviews] = useState([]);
  const [blockedAccounts, setBlockedAccounts] = useState([]);
  const [operations, setOperations] = useState([]);
  const initialTab = searchParams.get('tab');
  const [tab, setTabState] = useState(['duplicates', 'suspicious', 'reviewed', 'verified', 'blocked', 'operations'].includes(initialTab) ? initialTab : 'duplicates');
  const [showArchivedOperations, setShowArchivedOperations] = useState(searchParams.get('archived') === '1');
  const setTab = (value) => {
    setTabState(value);
    const next = new URLSearchParams(searchParams);
    next.set('tab', value);
    if (value !== 'operations') next.delete('archived');
    setSearchParams(next, { replace: true });
  };
  const [query, setQuery] = useState('');
  const [minScore, setMinScore] = useState('42');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [pageError, setPageError] = useState(null);
  const [dialog, setDialog] = useState(null);

  const loadRun = useCallback(async () => {
    const latest = await getLatestAccountAnalysisRun();
    setRun(latest);
    return latest;
  }, []);

  const loadFindings = useCallback(async (selectedRun) => {
    if (!selectedRun?.id || selectedRun.status !== 'completed') { setFindings([]); return; }
    const result = await getAccountAnalysisFindings({ runId: selectedRun.id, minScore: Number(minScore) || 0, pageSize: 200 });
    setFindings(Array.isArray(result?.items) ? result.items : []);
  }, [minScore]);

  const loadReviews = useCallback(async () => {
    const rows = await getAccountReviews({ take: 2000 });
    setReviews(Array.isArray(rows) ? rows : []);
  }, []);

  const loadBlocked = useCallback(async () => setBlockedAccounts(await getBlockedAccounts({ take: 2000 })), []);
  const loadOperations = useCallback(async () => setOperations(await getAccountOperations({ take: 200, archived: showArchivedOperations })), [showArchivedOperations]);

  const loadAll = useCallback(async () => {
    try {
      setLoading(true);
      const latest = await loadRun();
      await Promise.all([loadFindings(latest), loadReviews(), loadBlocked(), loadOperations()]);
      setPageError(null);
    } catch (error) {
      setPageError(handleApiError(error, notify, 'Не удалось загрузить менеджер аккаунтов'));
    } finally { setLoading(false); }
  }, [loadBlocked, loadFindings, loadOperations, loadReviews, loadRun, notify]);

  useEffect(() => { loadAll(); }, [loadAll]);

  useEffect(() => {
    const scanActive = run?.id && ACTIVE_RUN_STATUSES.has(run.status);
    const operationActive = operations.some((item) => ACTIVE_OPERATION_STATUSES.has(item.status));
    if (!scanActive && !operationActive) return undefined;
    const timer = window.setInterval(async () => {
      try {
        const tasks = [];
        if (scanActive) tasks.push(getAccountAnalysisRun(run.id).then((fresh) => setRun(fresh)));
        if (operationActive) tasks.push(loadOperations());
        await Promise.all(tasks);
        const freshOps = operationActive ? await getAccountOperations({ take: 200, archived: showArchivedOperations }) : operations;
        if (operationActive) setOperations(freshOps);
        if (operationActive && !freshOps.some((item) => ACTIVE_OPERATION_STATUSES.has(item.status))) await Promise.all([loadBlocked(), loadReviews(), loadFindings(run)]);
      } catch (error) { setPageError(handleApiError(error, notify, 'Не удалось обновить статус операций')); }
    }, 1800);
    return () => window.clearInterval(timer);
  }, [loadBlocked, loadFindings, loadOperations, loadReviews, notify, operations, run, showArchivedOperations]);

  const act = async (callback, successText, errorText) => {
    try {
      setBusy(true);
      await callback();
      if (successText) notify.success(successText);
      setPageError(null);
      await Promise.all([loadFindings(run), loadReviews(), loadBlocked(), loadOperations()]);
      return true;
    } catch (error) {
      setPageError(handleApiError(error, notify, errorText));
      return false;
    } finally { setBusy(false); }
  };

  const startScan = () => act(async () => {
    const created = await startAccountAnalysis();
    setRun(created);
    setFindings([]);
  }, 'Полный анализ аккаунтов запущен', 'Не удалось запустить анализ');

  const decision = (findingId, value) => act(
    () => decideAccountFinding(findingId, value),
    value === 'different' ? 'Пара помечена как разные люди' : value === 'duplicate' ? 'Дубль подтверждён' : 'Результат скрыт',
    'Не удалось сохранить решение',
  );

  const verify = (userId) => act(() => decideAccount(userId, 'verified'), 'Аккаунт помечен проверенным', 'Не удалось сохранить отметку');
  const unverify = async (userId) => {
    const ok = await notify.confirm({ title: 'Снять отметку?', message: 'Аккаунт снова сможет появляться среди подозрительных.', okText: 'Снять', cancelText: 'Отмена' });
    if (ok) await act(() => deleteAccountDecision(userId), 'Отметка снята', 'Не удалось снять отметку');
  };
  const resetFinding = async (findingId) => {
    const ok = await notify.confirm({ title: 'Снять решение по паре?', message: 'Пара снова появится в результатах анализа.', okText: 'Снять', cancelText: 'Отмена' });
    if (ok) await act(() => deleteAccountFindingDecision(findingId), 'Решение снято', 'Не удалось снять решение');
  };
  const removeReview = async (review) => {
    const ok = await notify.confirm({ title: 'Снять сохранённое решение?', message: 'После удаления решения аккаунт или пара снова смогут появиться в новых результатах анализа.', okText: 'Снять решение', cancelText: 'Отмена' });
    if (ok) await act(() => deleteAccountReview(review.id), 'Решение удалено', 'Не удалось удалить решение');
  };

  const openBlock = (account, blockRow = null) => setDialog({
    mode: 'block',
    account,
    editing: !!blockRow,
    reason: blockRow?.reason || account.blockReason || 'manual-review',
    note: blockRow?.note || account.blockNote || '',
    expiresAtLocal: '',
  });
  const openDelete = (account) => setDialog({ mode: 'delete', account, reason: '', confirmation: '', hardDelete: false });
  const openMerge = (finding, accounts) => setDialog({
    mode: 'merge', finding, accounts, suggestedPrimaryUserId: finding.data?.suggestedPrimaryUserId, primaryUserId: finding.data?.suggestedPrimaryUserId || accounts[0]?.userId, reason: 'Подтверждённый дубль', confirmation: '',
  });

  const unblock = async (account) => {
    const ok = await notify.confirm({ title: 'Разблокировать аккаунт?', message: `${accountTitle(account)} снова сможет войти на сайт.`, okText: 'Разблокировать', cancelText: 'Отмена' });
    if (ok) await act(() => unblockAccount(account.userId), 'Разблокировка поставлена в очередь', 'Не удалось разблокировать аккаунт');
  };

  const submitDialog = async (value) => {
    let success = false;
    if (value.mode === 'block') {
      success = await act(() => blockAccount(value.account.userId, {
        reason: value.reason,
        note: value.note,
        expiresAtUtc: value.expiresAtLocal ? new Date(value.expiresAtLocal).toISOString() : null,
      }), 'Блокировка поставлена в очередь', 'Не удалось заблокировать аккаунт');
    } else if (value.mode === 'delete') {
      success = await act(() => createAccountOperation({
        type: 'delete',
        sourceUserId: value.account.userId,
        reason: value.reason,
        confirmation: value.confirmation,
        hardDelete: !!value.hardDelete,
      }), 'Удаление поставлено в безопасную очередь', 'Не удалось запустить удаление');
    } else if (value.mode === 'merge') {
      success = await act(() => createAccountOperation({
        type: 'merge',
        sourceUserId: value.source.userId,
        targetUserId: value.primary.userId,
        findingId: value.finding?.id,
        reason: value.reason,
        confirmation: value.confirmation,
      }), 'Объединение аккаунтов запущено', 'Не удалось запустить объединение');
    } else if (value.mode === 'review') {
      success = await act(() => updateAccountReview(value.review.id, value.decision, value.note), 'Решение обновлено', 'Не удалось обновить решение');
    } else if (value.mode === 'operation') {
      success = await act(() => updateAccountOperation(value.operation.id, {
        reason: value.reason,
        note: value.note,
        expiresAtUtc: value.expiresAtLocal ? new Date(value.expiresAtLocal).toISOString() : null,
        clearExpiresAt: !value.expiresAtLocal,
        hardDelete: !!value.hardDelete,
      }), 'Операция обновлена', 'Не удалось обновить операцию');
    }
    if (success) { setDialog(null); setTab(value.mode === 'block' ? 'blocked' : value.mode === 'review' ? 'verified' : 'operations'); }
  };

  const operationHandlers = {
    retry: (id) => act(() => retryAccountOperation(id), 'Операция поставлена на повтор', 'Не удалось повторить операцию'),
    cancel: async (id) => {
      const ok = await notify.confirm({ title: 'Отменить операцию?', message: 'Операция ещё не началась, поэтому её можно безопасно снять с очереди.', okText: 'Отменить', cancelText: 'Назад' });
      if (ok) await act(() => cancelAccountOperation(id), 'Запрошена отмена операции', 'Не удалось отменить операцию');
    },
    archive: (id) => act(() => archiveAccountOperation(id), 'Операция архивирована', 'Не удалось архивировать операцию'),
    restore: (id) => act(() => restoreAccountOperation(id), 'Операция восстановлена из архива', 'Не удалось восстановить операцию'),
    edit: (operation) => setDialog({
      mode: 'operation',
      operation,
      reason: operation.reason || '',
      note: operation.options?.note || '',
      expiresAtLocal: toLocalDateTimeInput(operation.options?.expiresAtUtc),
      hardDelete: !!operation.options?.hardDelete,
    }),
  };

  const verifiedIds = useMemo(() => new Set(reviews.filter((row) => row.subjectType === 'account' && row.decision === 'verified' && row.userId).map((row) => String(row.userId))), [reviews]);
  const blockedIds = useMemo(() => new Set(blockedAccounts.map((row) => String(row.user?.userId || row.userId))), [blockedAccounts]);

  const filtered = useMemo(() => {
    const search = query.trim().toLowerCase();
    return findings.filter((item) => {
      const accountIds = item.kind === 'duplicate' ? (item.data?.accounts || []).map((account) => String(account.userId)) : [String(item.data?.account?.userId || item.primaryUserId || '')];
      const allAnchored = accountIds.length > 0 && accountIds.every((userId) => verifiedIds.has(userId) || blockedIds.has(userId));
      const singleAnchored = item.kind === 'suspicious' && accountIds.some((userId) => verifiedIds.has(userId) || blockedIds.has(userId));
      if (tab === 'duplicates' && (item.kind !== 'duplicate' || item.status !== 'open' || allAnchored)) return false;
      if (tab === 'suspicious' && (item.kind !== 'suspicious' || item.status !== 'open' || singleAnchored)) return false;
      if (!search) return true;
      return JSON.stringify(item.data || {}).toLowerCase().includes(search) || String(item.findingKey || '').toLowerCase().includes(search);
    });
  }, [blockedIds, findings, query, tab, verifiedIds]);

  const openDuplicates = findings.filter((item) => item.kind === 'duplicate' && item.status === 'open').length;
  const openSuspicious = findings.filter((item) => item.kind === 'suspicious' && item.status === 'open').length;
  const reviewedCount = reviews.filter((x) => !(x.subjectType === 'account' && x.decision === 'verified')).length;
  const verifiedCount = reviews.filter((x) => x.subjectType === 'account' && x.decision === 'verified').length;
  const activeRun = run && ACTIVE_RUN_STATUSES.has(run.status);

  const handlers = { decision, resetFinding, verify, unverify, openBlock, unblock, openDelete, openMerge };

  return (
    <div className="space-y-5 sm:space-y-6">
      <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
        <div className="min-w-0">
          <div className="mb-2 flex flex-wrap items-center gap-2 text-xs text-neutral-500"><span>ИИ</span><span>›</span><span>Менеджер аккаунтов</span></div>
          <h1 className="flex items-center gap-3 text-2xl font-semibold sm:text-3xl"><span className="flex h-11 w-11 items-center justify-center rounded-2xl border border-[rgb(var(--border))]"><BrainCircuit size={23} /></span>Менеджер аккаунтов</h1>
          <p className="mt-2 max-w-4xl text-sm text-neutral-500">Поиск дублей теперь связан с полным управлением жизненным циклом: проверка, блокировка, разблокировка, объединение, безопасное удаление и обратимое управление решениями.</p>
        </div>
        <div className="flex flex-wrap gap-2"><Button variant="outline" disabled={loading || busy} onClick={loadAll}><RefreshCcw size={16} /><span className="ml-1">Обновить</span></Button><Button disabled={busy || activeRun} onClick={startScan}>{activeRun ? <Loader2 size={16} className="animate-spin" /> : <ScanSearch size={16} />}<span className="ml-1">{activeRun ? 'Анализ идёт' : 'Проанализировать аккаунты'}</span></Button></div>
      </div>

      {pageError ? <AppErrorPanel error={pageError} title="Проблема менеджера аккаунтов" /> : null}
      <RunProgress run={run} />

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-5">
        <Metric label="Возможные дубли" value={run?.duplicateFindings ?? 0} hint={`${openDuplicates} требуют решения`} icon={Users} />
        <Metric label="Странные аккаунты" value={run?.suspiciousFindings ?? 0} hint={`${openSuspicious} требуют проверки`} icon={Fingerprint} />
        <Metric label="Проверенные якоря" value={verifiedCount} hint="Можно снять отметку" icon={ShieldCheck} />
        <Metric label="Заблокированные" value={blockedAccounts.length} hint="Отдельный реестр" icon={Ban} />
        <Metric label="Операции" value={operations.length} hint={`${operations.filter((x) => ACTIVE_OPERATION_STATUSES.has(x.status)).length} выполняются`} icon={History} />
      </div>

      <Card>
        <div className="flex flex-col gap-3 xl:flex-row xl:items-end xl:justify-between">
          <div className="flex flex-wrap gap-2">
            {[
              ['duplicates', `Дубли ${openDuplicates}`], ['suspicious', `Странные ${openSuspicious}`], ['reviewed', `Решения ${reviewedCount}`], ['verified', `Проверенные ${verifiedCount}`], ['blocked', `Заблокированные ${blockedAccounts.length}`], ['operations', `Операции ${operations.length}`],
            ].map(([key, label]) => <Button key={key} variant={tab === key ? 'default' : 'outline'} onClick={() => setTab(key)}>{label}</Button>)}
          </div>
          {['duplicates', 'suspicious'].includes(tab) ? <div className="grid grid-cols-1 gap-3 sm:grid-cols-[minmax(240px,1fr),160px] xl:w-[560px]"><Field label="Поиск"><Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="имя, логин, email, ID" /></Field><Field label="Минимальный балл"><Select value={minScore} onChange={(event) => setMinScore(event.target.value)}><option value="0">Все</option><option value="42">От 42</option><option value="60">От 60</option><option value="75">От 75</option><option value="90">От 90</option></Select></Field></div> : tab === 'reviewed' ? <div className="xl:w-[420px]"><Field label="Поиск по решениям"><Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="имя, ID, комментарий" /></Field></div> : null}
        </div>
      </Card>

      {loading ? <Card><div className="flex items-center gap-2 text-neutral-500"><Loader2 size={18} className="animate-spin" /> Загружаю данные…</div></Card> : null}
      {!loading && !run ? <Card><div className="py-8 text-center text-neutral-500"><Bot size={28} className="mx-auto mb-3" />Нажми «Проанализировать аккаунты», чтобы получить первый отчёт.</div></Card> : null}

      {!loading && tab === 'reviewed' ? <ReviewRegistry rows={reviews} query={query} busy={busy} onDelete={removeReview} onEdit={(review) => setDialog({ mode: 'review', review, decision: review.decision, note: review.note || '' })} /> : null}
      {!loading && tab === 'verified' ? <VerifiedAccounts rows={reviews} busy={busy} onReset={unverify} onEdit={(review) => setDialog({ mode: 'review', review, decision: review.decision, note: review.note || '' })} /> : null}
      {!loading && tab === 'blocked' ? <BlockedAccounts rows={blockedAccounts} busy={busy} handlers={{ openBlock, unblock, openDelete }} /> : null}
      {!loading && tab === 'operations' ? <><Card><div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between"><div><div className="font-semibold">Журнал операций</div><div className="mt-1 text-xs text-neutral-500">Архивирование не удаляет аудит. Любую архивную запись можно вернуть.</div></div><Button variant={showArchivedOperations ? 'default' : 'outline'} onClick={() => { const next = !showArchivedOperations; setShowArchivedOperations(next); const params = new URLSearchParams(searchParams); params.set('tab', 'operations'); if (next) params.set('archived', '1'); else params.delete('archived'); setSearchParams(params, { replace: true }); }}>{showArchivedOperations ? 'Показаны архивные' : 'Открыть архив'}</Button></div></Card><OperationsList rows={operations} busy={busy} handlers={operationHandlers} archived={showArchivedOperations} /></> : null}

      {!loading && ['duplicates', 'suspicious'].includes(tab) ? (
        <div className="space-y-4">
          {filtered.map((finding) => finding.kind === 'duplicate'
            ? <DuplicateFinding key={finding.id} finding={finding} busy={busy} verifiedIds={verifiedIds} blockedIds={blockedIds} handlers={handlers} />
            : <SuspiciousFinding key={finding.id} finding={finding} busy={busy} verifiedIds={verifiedIds} blockedIds={blockedIds} handlers={handlers} />)}
          {run?.status === 'completed' && filtered.length === 0 ? <Card><div className="py-8 text-center text-neutral-500"><CheckCircle2 size={28} className="mx-auto mb-3" />В этой вкладке сейчас ничего нет.</div></Card> : null}
        </div>
      ) : null}

      <Card className="border-dashed"><div className="flex gap-3"><History size={19} className="mt-0.5 shrink-0" /><div className="text-sm text-neutral-500">Все действия обратимы там, где это безопасно: решения можно снять, блокировку изменить или удалить, операции повторить после сбоя. Удаление и объединение идут по шагам через все сервисы и сохраняют журнал.</div></div></Card>

      <AccountActionModal dialog={dialog} setDialog={setDialog} onSubmit={submitDialog} busy={busy} />
    </div>
  );
}
