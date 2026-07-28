import React from 'react';
import { Button, Card } from '../../../components/ui';
import { ChoiceButton } from './SettingsPrimitives';

function BackgroundSettingsSection({ form, setField, options }) {
  return (
    <div className="space-y-3">
      <Card className="p-4 flex items-center justify-between gap-4">
        <div><div className="font-semibold">Фоновые эффекты</div><div className="text-sm text-neutral-500 dark:text-neutral-400">Включает или выключает живой фон. Выбор варианта доступен только после включения эффектов.</div></div>
        <Button variant={form.bgFx ? 'primary' : 'outline'} onClick={() => setField('bgFx', !form.bgFx)}>{form.bgFx ? 'Включено' : 'Выключено'}</Button>
      </Card>
      {!form.bgFx ? <Card className="p-3 text-sm text-neutral-500 dark:text-neutral-400">Сначала включи фоновые эффекты. После этого можно будет выбрать случайный режим или конкретный вариант.</Card> : null}
      <div className="grid gap-3 md:grid-cols-2">
        {options.map((option) => {
          const isRandom = option.key === 'random';
          const selected = isRandom ? form.fxMode === 'random' : form.fxMode === 'fixed' && String(form.fxVariant) === option.key;
          return (
            <ChoiceButton
              key={option.key}
              active={selected}
              disabled={!form.bgFx}
              title={option.title}
              desc={option.desc}
              onClick={() => {
                if (isRandom) setField('fxMode', 'random');
                else {
                  setField('fxMode', 'fixed');
                  setField('fxVariant', option.key);
                }
              }}
            />
          );
        })}
      </div>
    </div>
  );
}

export default React.memo(BackgroundSettingsSection);
