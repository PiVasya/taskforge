import React from 'react';
import { Card } from '../../../components/ui';
import { ChoiceButton } from './SettingsPrimitives';

function SolveSettingsSection({ form, setField }) {
  return (
    <div className="space-y-4">
      <Card className="p-4 space-y-4">
        <div className="font-semibold">Страница решения задач</div>
        <div className="grid gap-3 md:grid-cols-2">
          <ChoiceButton active={form.codeSolveLayout === 'split'} title="Классический split" onClick={() => setField('codeSolveLayout', 'split')} />
          <ChoiceButton active={form.codeSolveLayout === 'editorTop'} title="Редактор сверху" onClick={() => setField('codeSolveLayout', 'editorTop')} />
        </div>
      </Card>
      <Card className="p-4 space-y-4">
        <div className="font-semibold">Цвет редактора кода</div>
        <div className="grid gap-3 md:grid-cols-2">
          <ChoiceButton active={form.codeEditorStyle !== 'mono'} title="Крутой цветной редактор" onClick={() => setField('codeEditorStyle', 'color')} />
          <ChoiceButton active={form.codeEditorStyle === 'mono'} title="Простой чёрно-белый" onClick={() => setField('codeEditorStyle', 'mono')} />
        </div>
      </Card>
    </div>
  );
}

export default React.memo(SolveSettingsSection);
