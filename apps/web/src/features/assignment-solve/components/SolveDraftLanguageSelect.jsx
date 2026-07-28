import React from 'react';
import { Select } from '../../../components/ui';
import { getSolveDraftStore, useSolveDraft } from '../solveDraftStore';

function SolveDraftLanguageSelect({ assignmentId, languages, allowedLangs, className = '' }) {
  const draft = useSolveDraft(assignmentId);
  const store = getSolveDraftStore(assignmentId);
  return (
    <div className={className}>
      <label className="label">Язык</label>
      <Select value={draft.language} onChange={(event) => store?.setLanguage(event.target.value)}>
        {languages.map((language) => <option key={language.value} value={language.value}>{language.label}</option>)}
      </Select>
      {allowedLangs?.length > 0 ? <div className="mt-1 text-xs text-neutral-500">Языки ограничены курсом: {languages.map((language) => language.label).join(', ')}</div> : null}
    </div>
  );
}

export default React.memo(SolveDraftLanguageSelect);
