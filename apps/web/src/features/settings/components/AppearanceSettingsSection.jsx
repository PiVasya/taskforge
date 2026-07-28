import React from 'react';
import { Button, Card } from '../../../components/ui';

function AppearanceSettingsSection({ form, setField }) {
  return (
    <div className="space-y-4">
      <Card className="p-4 space-y-4">
        <div><div className="font-semibold">Режим</div><div className="text-sm text-neutral-500 dark:text-neutral-400">Светлая или тёмная тема.</div></div>
        <div className="flex flex-wrap gap-2">
          <Button variant={form.mode === 'light' ? 'primary' : 'outline'} onClick={() => setField('mode', 'light')}>Светлая</Button>
          <Button variant={form.mode === 'dark' ? 'primary' : 'outline'} onClick={() => setField('mode', 'dark')}>Тёмная</Button>
        </div>
      </Card>
      <Card className="p-4 space-y-4">
        <div><div className="font-semibold">Палитра</div><div className="text-sm text-neutral-500 dark:text-neutral-400">Цвет акцентов и кнопок.</div></div>
        <div className="flex flex-wrap gap-2">
          <Button variant={form.colorTheme === 'blue' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'blue')}>Синяя</Button>
          <Button variant={form.colorTheme === 'pink' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'pink')}>Розовая</Button>
          <Button variant={form.colorTheme === 'apple' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'apple')}>Яблоко</Button>
          <Button variant={form.colorTheme === 'red' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'red')}>Красная</Button>
          <Button variant={form.colorTheme === 'honey' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'honey')}>Мёд</Button>
          <Button variant={form.colorTheme === 'violet' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'violet')}>Фиолетовая</Button>
        </div>
      </Card>
      <Card className="p-4 space-y-4">
        <div><div className="font-semibold">Левое меню</div><div className="text-sm text-neutral-500 dark:text-neutral-400">Основную навигацию можно свернуть до иконок через стрелку рядом с TaskForge.</div></div>
        <div className="flex flex-wrap gap-2">
          <Button variant={form.showSidebarToggle !== false ? 'primary' : 'outline'} onClick={() => setField('showSidebarToggle', true)}>Показывать стрелку</Button>
          <Button variant={form.showSidebarToggle === false ? 'primary' : 'outline'} onClick={() => setField('showSidebarToggle', false)}>Скрыть стрелку</Button>
        </div>
      </Card>
    </div>
  );
}

export default React.memo(AppearanceSettingsSection);
