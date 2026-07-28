import React from 'react';
import { Card } from '../../../components/ui';
import { ChoiceButton } from './SettingsPrimitives';

function SolveSettingsSection({ form, setField }) {
  return (
    <div className="space-y-4">
      <Card className="p-4 space-y-4">
        <div><div className="font-semibold">Страница решения задач</div><div className="text-sm text-neutral-500 dark:text-neutral-400">Выберите, как будет выглядеть страница решения задач с кодом.</div></div>
        <div className="grid gap-3 md:grid-cols-2">
          <ChoiceButton active={form.codeSolveLayout === 'split'} title="Классический split" desc="Условие слева, редактор справа. Удобно, когда надо постоянно видеть текст задания." onClick={() => setField('codeSolveLayout', 'split')} />
          <ChoiceButton active={form.codeSolveLayout === 'editorTop'} title="Редактор сверху" desc="Поле кода на всю ширину, условие и публичные тесты ниже." onClick={() => setField('codeSolveLayout', 'editorTop')} />
        </div>
      </Card>
      <Card className="p-4 space-y-4">
        <div><div className="font-semibold">Цвет редактора кода</div><div className="text-sm text-neutral-500 dark:text-neutral-400">Цветной вариант подстраивается под выбранную палитру. Чёрно-белый оставляет спокойный редактор без яркой подсветки.</div></div>
        <div className="grid gap-3 md:grid-cols-2">
          <ChoiceButton active={form.codeEditorStyle !== 'mono'} title="Крутой цветной редактор" desc="Подсветка синтаксиса использует цвета текущей темы." onClick={() => setField('codeEditorStyle', 'color')} />
          <ChoiceButton active={form.codeEditorStyle === 'mono'} title="Простой чёрно-белый" desc="Минимум цвета, фон и текст берутся из текущей темы." onClick={() => setField('codeEditorStyle', 'mono')} />
        </div>
      </Card>
    </div>
  );
}

export default React.memo(SolveSettingsSection);
