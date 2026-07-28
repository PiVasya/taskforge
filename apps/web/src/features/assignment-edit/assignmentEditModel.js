export function normalizeCodeTestCases(source) {
  if (Array.isArray(source)) return source;
  if (source && typeof source === 'object') {
    const publicTests = Array.isArray(source.publicTests) ? source.publicTests.map((test) => ({ ...test, isHidden: false })) : [];
    const hiddenTests = Array.isArray(source.hiddenTests) ? source.hiddenTests.map((test) => ({ ...test, isHidden: true })) : [];
    const nested = Array.isArray(source.testCases) ? source.testCases : Array.isArray(source.tests) ? source.tests : Array.isArray(source.cases) ? source.cases : [];
    return [...publicTests, ...hiddenTests, ...nested];
  }
  return [];
}

export const LANGS_BY_TYPE = {
  'code-test': [
    { value: 'cpp', label: 'C++' },
    { value: 'python', label: 'Python' },
    { value: 'csharp', label: 'C#' },
    { value: 'javascript', label: 'JavaScript' },
    { value: 'pascal', label: 'Pascal' },
    { value: 'java', label: 'Java' },
  ],
  'image-test': [
    { value: 'python', label: 'Python Turtle / matplotlib' },
    { value: 'pascal', label: 'Pascal GraphABC' },
    { value: 'cpp', label: 'C++ GLUT / Turtle' },
  ],
};

export const DEFAULT_ANALYTICS_SETTINGS = {
  mode: 'basic',
  trackOpen: true,
  trackAttempts: true,
  trackTime: true,
  trackLanguage: true,
  trackErrors: true,
  trackEditorChanges: false,
  trackClipboard: false,
  trackFocus: false,
  trackVisibility: false,
  trackFullscreen: false,
  trackCodeSnapshots: false,
  trackRiskScore: false,
  trackLiveActivity: false,
  trackSimilarity: false,
  storeFullCode: false,
  storePasteText: false,
  storeTextSamples: true,
  pasteSampleLimit: 500,
  codeSampleLimit: 1000,
  codeSnapshotIntervalSeconds: 45,
  eventBatchIntervalSeconds: 10,
  retentionDays: 30,
  maxFullCodeLength: 80000,
  maxEventsPerBatch: 120,
};

export const ANALYTICS_MODE_LABELS = {
  off: 'выключена',
  basic: 'базовая',
  solution: 'решения',
  proctoring: 'proctoring',
  custom: 'кастом',
};
