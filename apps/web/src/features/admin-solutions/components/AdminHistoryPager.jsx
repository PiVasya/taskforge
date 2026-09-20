import React from 'react';
import { Badge, Button } from '../../../components/ui';

export default function AdminHistoryPager({ label, page, total, pageSize, onPage }) {
  const safeTotal = Math.max(0, Number(total) || 0);
  const totalPages = Math.max(1, Math.ceil(safeTotal / pageSize));
  const safePage = Math.min(Math.max(1, Number(page) || 1), totalPages);
  const from = safeTotal === 0 ? 0 : (safePage - 1) * pageSize + 1;
  const to = safeTotal === 0 ? 0 : Math.min(safeTotal, safePage * pageSize);

  return (
    <div
      className="flex flex-wrap items-center justify-between gap-2 text-sm"
      data-testid={`admin-${label}-pager`}
      data-page={safePage}
      data-page-size={pageSize}
      data-total={safeTotal}
      data-total-pages={totalPages}
    >
      <div className="text-neutral-600 dark:text-neutral-300">
        Показано {from}–{to} из {safeTotal}
      </div>
      <div className="flex items-center gap-2">
        <Button variant="outline" onClick={() => onPage(safePage - 1)} disabled={safePage <= 1}>Назад</Button>
        <Badge intent="secondary">Страница {safePage} / {totalPages}</Badge>
        <Button variant="outline" onClick={() => onPage(safePage + 1)} disabled={safePage >= totalPages}>Дальше</Button>
      </div>
    </div>
  );
}
