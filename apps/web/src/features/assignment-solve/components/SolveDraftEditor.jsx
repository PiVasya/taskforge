import React, { useCallback } from 'react';
import CodeEditor from '../../../components/CodeEditor';
import { Button, Select } from '../../../components/ui';
import { getSolveDraftStore, useSolveDraft } from '../solveDraftStore';

function SolveDraftEditor({
  assignmentId,
  starterCode = '',
  langsForSelect,
  allowedLangs,
  height = 380,
  showLanguage = true,
  showAllowedHint = true,
  onReset,
}) {
  const draft = useSolveDraft(assignmentId);
  const store = getSolveDraftStore(assignmentId);
  const hasStarterCode = typeof starterCode === 'string' && starterCode.length > 0;
  const canReset = hasStarterCode && draft.code !== starterCode;

  const reset = useCallback(() => {
    if (!store || !canReset) return;
    if (draft.code.trim() && !window.confirm('Вернуть исходную заготовку? Текущий код будет заменён.')) return;
    store.setCode(starterCode);
    onReset?.();
  }, [canReset, draft.code, onReset, starterCode, store]);

  return (
    <div className="grid gap-3">
      {showLanguage ? (
        <div>
          <label className="label">Язык</label>
          <Select
            value={draft.language}
            onChange={(event) => store?.setLanguage(event.target.value)}
            data-taskforge-automation-id="solution-language"
            data-taskforge-agent-role="solution-language"
            data-taskforge-agent-action="select-language"
            aria-label="Язык решения"
          >
            {langsForSelect.map((language) => <option key={language.value} value={language.value}>{language.label}</option>)}
          </Select>
          {showAllowedHint && allowedLangs?.length > 0 ? (
            <div className="mt-1 text-xs text-neutral-500">Языки ограничены курсом: {langsForSelect.map((language) => language.label).join(', ')}</div>
          ) : null}
        </div>
      ) : null}
      <div>
        <div className="mb-1 flex items-center justify-between gap-3">
          <label className="label mb-0">Ваш код</label>
          {hasStarterCode ? <Button type="button" variant="ghost" className="px-2 py-1 text-xs" onClick={reset} disabled={!canReset}>Вернуть заготовку</Button> : null}
        </div>
        <CodeEditor
          modelPath={`taskforge://solve/${assignmentId}`}
          language={draft.language}
          value={draft.code}
          onChange={(value) => store?.setCode(value || '')}
          height={height}
          automationId="solution-code-editor"
          automationRole="code-editor"
          automationAction="fill-solution-code"
        />
      </div>
    </div>
  );
}

export default React.memo(SolveDraftEditor);
