import React from 'react';
import { LayoutGrid, Workflow } from 'lucide-react';
import { Button } from '../../../components/ui';

export default function CourseLayoutToggle({ value, onChange }) {
  return (
    <div className="flex min-w-0 items-center gap-2 rounded-2xl border border-[rgba(var(--border)/0.55)] bg-[rgba(var(--muted)/0.14)] p-1">
      <Button
        variant={value === 'flow' ? 'primary' : 'ghost'}
        className="min-w-0 flex-1 px-3 py-2 text-xs sm:flex-none"
        onClick={() => onChange('flow')}
        title="Интерактивная карта курса"
      >
        <Workflow size={15} /> Карта
      </Button>
      <Button
        variant={value === 'grid' ? 'primary' : 'ghost'}
        className="min-w-0 flex-1 px-3 py-2 text-xs sm:flex-none"
        onClick={() => onChange('grid')}
        title="Обычные карточки"
      >
        <LayoutGrid size={15} /> Карточки
      </Button>
    </div>
  );
}
