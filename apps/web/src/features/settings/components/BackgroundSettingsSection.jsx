import React from 'react';
import { Button, Card } from '../../../components/ui';
import { ChoiceButton } from './SettingsPrimitives';

function BackgroundSettingsSection({ form, setField, options }) {
  return (
    <div className="space-y-3">
      <Card className="p-4 flex items-center justify-between gap-4">
        <div className="font-semibold">Фоновые эффекты</div>
        <Button variant={form.bgFx ? 'primary' : 'outline'} onClick={() => setField('bgFx', !form.bgFx)}>{form.bgFx ? 'Включено' : 'Выключено'}</Button>
      </Card>
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
