const LANGUAGE_LABELS = Object.freeze({
  cpp: 'C++',
  csharp: 'C#',
  python: 'Python',
  javascript: 'JS',
  pascal: 'Pascal',
  java: 'Java',
});

const LANGUAGE_ALIASES = Object.freeze({
  'c++': 'cpp',
  'g++': 'cpp',
  gcc: 'cpp',
  cxx: 'cpp',
  'си++': 'cpp',
  'с++': 'cpp',
  'c#': 'csharp',
  cs: 'csharp',
  sharp: 'csharp',
  'си#': 'csharp',
  'с#': 'csharp',
  'шарп': 'csharp',
  py: 'python',
  python3: 'python',
  'питон': 'python',
  js: 'javascript',
  node: 'javascript',
  nodejs: 'javascript',
  'node.js': 'javascript',
  'java-script': 'javascript',
  pas: 'pascal',
  pascalabc: 'pascal',
  pascalabcnet: 'pascal',
  'джава': 'java',
});

export function normalizeCourseMapLanguage(value) {
  const source = String(value || '').trim();
  if (!source) return '';
  const normalized = source.toLowerCase();
  return LANGUAGE_ALIASES[normalized] || normalized;
}

export function getCourseMapLanguageLabel(value) {
  const source = String(value || '').trim();
  if (!source) return '';
  const normalized = normalizeCourseMapLanguage(source);
  return LANGUAGE_LABELS[normalized] || source;
}

export function getAssignmentProgrammingLanguage(assignment) {
  const direct = String(assignment?.language || '').trim();
  if (direct) return direct;
  if (!Array.isArray(assignment?.allowedLanguages)) return '';
  return String(assignment.allowedLanguages.find((value) => String(value || '').trim()) || '').trim();
}


export function getCourseMapCodeTerminalModel(assignment) {
  const entity = assignment || {};
  const title = String(entity?.title || '').trim() || 'Без названия';
  const languageLabel = getCourseMapLanguageLabel(getAssignmentProgrammingLanguage(entity));
  const solved = Boolean(
    entity?.solvedByCurrentUser
      || entity?.isSolved
      || entity?.completedByCurrentUser
      || entity?.progressStatus === 'solved'
  );

  return {
    title,
    languageLabel,
    solved,
    statusLabel: solved ? 'решено' : '',
  };
}

export function getCourseMapAssignmentFooterLabel(type, assignment, fallback = '') {
  const assignmentType = String(type || '').trim().toLowerCase();
  if (assignmentType === 'sql-test') return '';

  if (assignmentType === 'code-test' || assignmentType === 'image-test') {
    return getCourseMapLanguageLabel(getAssignmentProgrammingLanguage(assignment)) || String(fallback || '').trim();
  }

  return String(fallback || '').trim();
}
