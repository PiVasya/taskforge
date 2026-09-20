import React from 'react';
import { Badge, Button, Card } from '../../../components/ui';
import { Trash2 } from 'lucide-react';
import CodeEditor from '../../../components/CodeEditor';
import AdminHistoryPager from './AdminHistoryPager';
import { CompactEmpty, RunnerOutput } from './AdminSolutionViews';
import { AssignmentLinkButton } from './AdminSolutionLiveCard';
import {
  formatDateTime,
  getAssignmentId,
  getSolutionBadgeIntent,
  getSolutionCode,
  getSolutionPassedFailed,
  getSolutionStatusLabel,
  getSolutionSubmittedAt,
  getSolutionTitle,
} from '../../../utils/solutionDto';

export default function AdminCodeSolutionsPanel({
  solutions,
  total,
  page,
  pageSize,
  onPage,
  userId,
  detailsMap,
  detailsLoadingMap,
  expandedCodeIds,
  bulkCodeLoading,
  onExpandPageCodes,
  onCollapsePageCodes,
  onToggleCode,
  onDeleteSolution,
}) {
  const anyExpanded = solutions.some((item) => expandedCodeIds.includes(item.id));

  return (
    <Card className="p-4 space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <AdminHistoryPager label="code" page={page} total={total} pageSize={pageSize} onPage={onPage} />
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" onClick={onExpandPageCodes} disabled={bulkCodeLoading}>
            {bulkCodeLoading ? 'Загружаю код…' : 'Показать код всей страницы'}
          </Button>
          <Button variant="outline" onClick={onCollapsePageCodes} disabled={!anyExpanded}>
            Скрыть код страницы
          </Button>
        </div>
      </div>

      <div className="space-y-6">
        {solutions.map((item) => {
          const full = detailsMap[item.id] || null;
          const expanded = expandedCodeIds.includes(item.id);
          const loadingDetails = !!detailsLoadingMap[item.id];
          const effective = full || item;
          const code = getSolutionCode(effective);
          const { passed, failed } = getSolutionPassedFailed(effective);

          return (
            <div
              key={item.id}
              className="border border-neutral-200 dark:border-neutral-800/40 rounded-xl p-4 bg-[rgb(var(--card))]"
              data-solution-id={item.id}
              data-user-id={item.userId || item.UserId || userId}
              data-assignment-id={getAssignmentId(effective)}
              data-verdict={effective.status || effective.verdict || ''}
            >
              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-2">
                <div className="min-w-0">
                  <div className="font-medium text-neutral-900 dark:text-neutral-50 truncate">{getSolutionTitle(effective)}</div>
                  <div className="text-xs text-neutral-600 dark:text-neutral-400">
                    {formatDateTime(getSolutionSubmittedAt(effective))} • {effective.language || effective.Language || '—'}
                  </div>
                </div>
                <div className="flex flex-wrap gap-2 items-center">
                  <Badge intent={getSolutionBadgeIntent(effective)}>{getSolutionStatusLabel(effective)}</Badge>
                  {passed !== null || failed !== null ? (
                    <Badge intent="secondary">Пройдено: {passed ?? 0} / Провалено: {failed ?? 0}</Badge>
                  ) : null}
                  <AssignmentLinkButton assignmentId={getAssignmentId(effective)} />
                  <Button variant="outline" className="inline-flex items-center gap-2" onClick={() => onToggleCode(item.id)} disabled={loadingDetails}>
                    {expanded ? 'Скрыть код' : 'Показать код'}
                  </Button>
                  <Button variant="outline" intent="danger" className="inline-flex items-center gap-2" onClick={() => onDeleteSolution(item.id)} title="Удалить это решение">
                    <Trash2 size={16} />
                  </Button>
                </div>
              </div>

              {expanded ? (
                <div className="mt-3 space-y-3">
                  {loadingDetails ? <CompactEmpty>Загружаю детали решения…</CompactEmpty> : null}
                  {!loadingDetails && full ? (
                    code ? (
                      <div className="rounded-xl overflow-hidden border border-neutral-700">
                        <CodeEditor language={full.language || full.Language || item.language || 'text'} value={code} readOnly onChange={() => {}} height={360} />
                      </div>
                    ) : <CompactEmpty>Код не найден для этого решения.</CompactEmpty>
                  ) : null}
                  {!loadingDetails && full ? <RunnerOutput item={full} /> : null}
                </div>
              ) : null}
            </div>
          );
        })}
      </div>
    </Card>
  );
}
