import React from 'react';
import { Button, Card } from '../../../components/ui';

function StyleSwitch({ enabled, onChange }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={enabled}
      aria-label="Необрутализм"
      onClick={() => onChange(!enabled)}
      className={`tf-style-switch ${enabled ? 'is-on' : ''}`}
    >
      <span className="tf-style-switch__thumb" />
    </button>
  );
}

function AppearanceSettingsSection({ form, setField }) {
  const neobrutal = form.uiStyle === 'neobrutal';

  return (
    <div className="space-y-4">
      <Card className="p-4 space-y-4">
        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="min-w-0">
            <div className="font-semibold">Необрутализм</div>
          </div>
          <div className="flex shrink-0 items-center gap-3">
            <span className="text-sm font-semibold">{neobrutal ? 'Включён' : 'Выключен'}</span>
            <StyleSwitch enabled={neobrutal} onChange={(value) => setField('uiStyle', value ? 'neobrutal' : 'default')} />
          </div>
        </div>
        <div className="tf-neobrutal-preview" aria-hidden="true">
          <div className="tf-neobrutal-preview__card tf-neobrutal-preview__card--cyan">TASK</div>
          <div className="tf-neobrutal-preview__card tf-neobrutal-preview__card--yellow">RUN</div>
          <div className="tf-neobrutal-preview__card tf-neobrutal-preview__card--pink">OK!</div>
        </div>
      </Card>

      <Card className="p-4 space-y-4">
        <div className="font-semibold">Режим</div>
        <div className="flex flex-wrap gap-2">
          <Button variant={form.mode === 'light' ? 'primary' : 'outline'} onClick={() => setField('mode', 'light')}>Светлая</Button>
          <Button variant={form.mode === 'dark' ? 'primary' : 'outline'} onClick={() => setField('mode', 'dark')}>Тёмная</Button>
        </div>
      </Card>
      <Card className="p-4 space-y-4">
        <div className="font-semibold">Палитра</div>
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
        <div className="font-semibold">Левое меню</div>
        <div className="flex flex-wrap gap-2">
          <Button variant={form.showSidebarToggle !== false ? 'primary' : 'outline'} onClick={() => setField('showSidebarToggle', true)}>Показывать стрелку</Button>
          <Button variant={form.showSidebarToggle === false ? 'primary' : 'outline'} onClick={() => setField('showSidebarToggle', false)}>Скрыть стрелку</Button>
        </div>
      </Card>
    </div>
  );
}

export default React.memo(AppearanceSettingsSection);
