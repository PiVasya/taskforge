const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..', 'src');

function fail(message) {
  console.error(`Frontend architecture invariant failed: ${message}`);
  process.exit(1);
}

function walk(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const fullPath = path.join(directory, entry.name);
    return entry.isDirectory() ? walk(fullPath) : [fullPath];
  });
}

function read(relativePath) {
  const fullPath = path.join(root, relativePath);
  if (!fs.existsSync(fullPath)) fail(`missing src/${relativePath}`);
  return fs.readFileSync(fullPath, 'utf8');
}

const requiredFiles = [
  'app/AppShell.jsx',
  'app/PublicShell.jsx',
  'app/AppProviders.jsx',
  'components/shell/PersistentBackground.jsx',
  'components/shell/AppHeader.jsx',
  'components/shell/DesktopSidebar.jsx',
  'components/shell/MobileNavigation.jsx',
  'components/shell/AdminNavigation.jsx',
  'components/shell/PageContent.jsx',
  'contexts/UiSettingsContext.jsx',
  'contexts/QuotaContext.jsx',
  'data/queryClient.js',
  'data/QueryClientProvider.jsx',
  'hooks/useQuery.js',
  'features/assignment-solve/AssignmentSolveFeature.jsx',
  'features/assignment-solve/assignmentSolveSupport.js',
  'features/assignment-solve/imageTaskModel.js',
  'features/assignment-solve/components/AssignmentSolvePresentation.jsx',
  'features/assignment-solve/solveDraftStore.js',
  'features/attempts/attemptAnswerStore.js',
  'features/attempts/AttemptCountdown.jsx',
  'features/task-test/TaskTestQuestion.jsx',
  'features/math-task/MathTaskBlock.jsx',
  'features/settings/SettingsFeature.jsx',
  'features/agent/AgentFeature.jsx',
  'features/agent/components/AgentComposer.jsx',
  'features/course-assignments/CourseAssignmentsFeature.jsx',
  'features/assignment-edit/AssignmentEditFeature.jsx',
  'features/admin-solutions/useAdminSolutionsData.js',
];
requiredFiles.forEach(read);

if (fs.existsSync(path.join(root, 'components', 'Layout.jsx'))) {
  fail('legacy page-owned Layout.jsx returned');
}

const sourceFiles = walk(root).filter((file) => /\.(js|jsx)$/.test(file));
const source = sourceFiles.map((file) => [file, fs.readFileSync(file, 'utf8')]);

for (const [file, text] of source) {
  const relative = path.relative(root, file);
  if (relative.startsWith(`pages${path.sep}`) && /components\/Layout/.test(text)) {
    fail(`page imports the legacy layout: src/${relative}`);
  }
  if (/\buiRev\b/.test(text)) {
    fail(`route-era uiRev background reset returned in src/${relative}`);
  }
}

const quotaProviderConsumers = source
  .filter(([file, text]) => !file.endsWith(path.join('contexts', 'QuotaContext.jsx')) && /<QuotaProvider\b/.test(text))
  .map(([file]) => path.relative(root, file));
if (quotaProviderConsumers.length !== 1 || quotaProviderConsumers[0] !== path.join('app', 'AppProviders.jsx')) {
  fail(`QuotaProvider must exist once in AppProviders; found: ${quotaProviderConsumers.join(', ') || 'none'}`);
}

const queryProviderConsumers = source
  .filter(([file, text]) => !file.endsWith(path.join('data', 'QueryClientProvider.jsx')) && /<QueryClientProvider\b/.test(text))
  .map(([file]) => path.relative(root, file));
if (queryProviderConsumers.length !== 1 || queryProviderConsumers[0] !== path.join('app', 'AppProviders.jsx')) {
  fail(`QueryClientProvider must exist once in AppProviders; found: ${queryProviderConsumers.join(', ') || 'none'}`);
}

const backgroundConsumers = source
  .filter(([file, text]) => !file.endsWith(path.join('components', 'bgfx', 'BgFxCanvas.jsx')) && /from ['"].*BgFxCanvas['"]/.test(text))
  .map(([file]) => path.relative(root, file));
if (backgroundConsumers.length !== 1 || backgroundConsumers[0] !== path.join('components', 'shell', 'PersistentBackground.jsx')) {
  fail(`BgFxCanvas must be owned only by PersistentBackground; found: ${backgroundConsumers.join(', ') || 'none'}`);
}

const app = read('App.jsx');
if (!/lazy\(\(\) => import\(/.test(app)) fail('route-level lazy chunks are missing');
if (!/<Route element={<RootShell \/>}>/.test(app)) fail('persistent RootShell route is missing');

const index = read('index.js');
if (!/<AppProviders>/.test(index)) fail('global providers are not mounted above the route tree');
if (!/styles\/theme\.css/.test(index) || !/components\/shell\/shell\.css/.test(index)) {
  fail('split CSS modules are not loaded by index.js');
}

const globalCss = read('index.css').trim().split(/\r?\n/).filter(Boolean);
if (globalCss.length > 6) fail('index.css became monolithic again');

const quota = read('contexts/QuotaContext.jsx');
if (/setInterval\(/.test(quota)) fail('QuotaProvider owns a per-provider interval instead of the shared second clock');
if (!/TasksQuotaContext/.test(quota) || !/TopQuotaContext/.test(quota)) {
  fail('quota buckets are not split into independent render domains');
}

const persistentBackground = read('components/shell/PersistentBackground.jsx');
if (!/useUiBackgroundSettings/.test(persistentBackground) || /useUiAppearance/.test(persistentBackground)) {
  fail('PersistentBackground subscribes to settings unrelated to the background');
}

const queryClient = read('data/queryClient.js');
if (!/AbortController/.test(queryClient) || !/entry\.promise/.test(queryClient) || !/isInvalidated/.test(queryClient)) {
  fail('query client lost cancellation, deduplication, or stale invalidation support');
}

const featureLineLimits = {
  'features/assignment-solve/AssignmentSolveFeature.jsx': 1700,
  'features/assignment-edit/AssignmentEditFeature.jsx': 1500,
  'features/course-assignments/CourseAssignmentsFeature.jsx': 1400,
  'features/settings/SettingsFeature.jsx': 600,
  'features/agent/AgentFeature.jsx': 800,
  'features/admin-solutions/AdminSolutionsFeature.jsx': 1000,
  'pages/TaskTestSolve.jsx': 350,
  'pages/MathTaskSolve.jsx': 350,
};
for (const [relativePath, maximumLines] of Object.entries(featureLineLimits)) {
  const lineCount = read(relativePath).split(/\r?\n/).length;
  if (lineCount > maximumLines) {
    fail(`feature controller became monolithic again: src/${relativePath} has ${lineCount} lines (limit ${maximumLines})`);
  }
}

const assignmentFeature = read('features/assignment-solve/AssignmentSolveFeature.jsx');
if (/<CodeEditor[\s\S]{0,240}?\bkey=/.test(assignmentFeature)) {
  fail('CodeEditor is keyed and can be remounted during assignment changes');
}
if (/const \[code, setCode\] = useState/.test(assignmentFeature) || /const \[language, setLanguage\] = useState/.test(assignmentFeature)) {
  fail('assignment draft returned to the large route component');
}
if (!/SolveDraftEditor/.test(assignmentFeature) || !/getSolveDraftSnapshot/.test(assignmentFeature)) {
  fail('assignment editor is not isolated behind the external draft store');
}

const taskTest = read('pages/TaskTestSolve.jsx');
const mathTask = read('pages/MathTaskSolve.jsx');
if (/const \[answers, setAnswers\] = useState/.test(taskTest) || /const \[answers, setAnswers\] = useState/.test(mathTask)) {
  fail('test/math answer maps returned to page state and will rerender entire attempts per keypress');
}
if (!/TaskTestQuestion/.test(taskTest) || !/MathTaskBlock/.test(mathTask) || !/AttemptCountdown/.test(taskTest + mathTask)) {
  fail('attempt questions or countdown are not isolated into narrow render components');
}

const agent = read('features/agent/AgentFeature.jsx');
if (/const \[text, setText\] = useState/.test(agent) || /const \[files, setFiles\] = useState/.test(agent)) {
  fail('agent composer input returned to the large conversation feature');
}
if (!/AgentComposer/.test(agent)) fail('agent composer boundary is missing');

const pageWrappers = [
  'pages/AssignmentSolvePage.jsx',
  'pages/AssignmentEditPage.jsx',
  'pages/CourseAssignmentsPage.jsx',
  'pages/SettingsPage.jsx',
  'pages/AgentPage.jsx',
  'pages/admin/AdminAnalyticsPage.jsx',
  'pages/admin/AdminSolutionsPage.jsx',
];
for (const relativePath of pageWrappers) {
  const text = read(relativePath);
  if (text.split(/\r?\n/).filter((line) => line.trim()).length > 4) {
    fail(`route wrapper contains feature logic again: src/${relativePath}`);
  }
}

const queryUsers = source.filter(([, text]) => /\buseQuery\s*\(/.test(text)).length;
if (queryUsers < 8) fail(`shared query layer is not used broadly enough; found ${queryUsers} query consumers`);

const authContext = read('auth/AuthContext.jsx');
if (/getMyUiSettings|tf-ui-settings-changed|UI_SETTINGS_KEY/.test(authContext)) {
  fail('AuthContext owns UI appearance side effects instead of authentication only');
}


const contextMenuCore = read('components/ui/ContextMenu.jsx');
if (!/claimContextMenuEvent/.test(contextMenuCore) || !/isNativeContextMenuTarget/.test(contextMenuCore)) {
  fail('shared context-menu event arbitration is missing');
}
const courseContentCard = read('features/course-assignments/components/CourseContentCard.jsx');
if (!/onContextMenuCapture/.test(courseContentCard)) {
  fail('course content cards lost capture-phase context menus');
}

const courseFlowEditor = read('features/course-assignments/components/CourseFlowEditor.jsx');
if (/\bMiniMap\b|course-map-minimap/.test(courseFlowEditor)) {
  fail('course map minimap returned');
}
if (!/courseProgressVersion/.test(courseFlowEditor) || !/readCourseProgress/.test(courseFlowEditor)) {
  fail('learner course nodes no longer use full server-side progress');
}
if (/panOnDrag=\{editorMode \? \[1,\s*2\]/.test(courseFlowEditor)) {
  fail('course map reserves the right mouse button for panning and breaks pane context menus');
}
if (/application\/x-taskforge-unplaced|onUnplacedDragStart|onMapDrop/.test(courseFlowEditor)) {
  fail('unplaced course-map entities returned to drag-and-drop placement');
}

if (!/streamLearningCourseMap/.test(courseFlowEditor) || !/getLearningCourseMapDelta/.test(courseFlowEditor)) {
  fail('learner course map lost streamed initial projection or incremental delta refresh');
}
if (!/persistLearnerGraphRef\.current\?\.\(\)/.test(courseFlowEditor) || !/projectionTokenRef\.current = ['"]['"]/.test(courseFlowEditor)) {
  fail('partial learner-map streams are not persisted safely before navigation');
}
if (/\bgetLearningCourseMap\s*\(/.test(courseFlowEditor)) {
  fail('learner course map returned to the legacy whole-map endpoint');
}
const courseMapCache = read('features/course-assignments/courseMapLocalCache.js');
if (!/indexedDB/.test(courseMapCache) || !/readCourseMapLocalCacheAsync/.test(courseMapCache)) {
  fail('learner course map lost IndexedDB persistence');
}
if (/\bgetLearningCourseMap\s*\(|\bgetAssignmentsByCourseTree\b/.test(assignmentFeature)) {
  fail('assignment solve returned to full-map/tree refreshes for next-node navigation');
}
const authSource = read('auth/AuthContext.jsx');
if (!/retryTransient/.test(authSource) || /catch\s*\{\s*setUser\(null\)/.test(authSource)) {
  fail('authentication can again turn a transient profile failure into an immediate logout');
}
const httpSource = read('api/http.js');
if (!/API_TELEMETRY_SLOW_MS/.test(httpSource) || /action:\s*isError\s*\?\s*['"]api-error['"]\s*:\s*['"]api-request['"]/.test(httpSource)) {
  fail('per-request success telemetry returned and can amplify API request storms');
}

const coursesPage = read('pages/CoursesPage.jsx');
if (!/!selectedCourse && !editorTools/.test(coursesPage)) {
  fail('courses catalog can open an empty context menu on blank space for viewers');
}

const courseMapNodes = [
  'features/course-assignments/nodes/CourseNode.jsx',
  'features/course-assignments/nodes/CodeTestNode.jsx',
  'features/course-assignments/nodes/TestNode.jsx',
  'features/course-assignments/nodes/ImageCodeNode.jsx',
  'features/course-assignments/nodes/MathNode.jsx',
  'features/course-assignments/nodes/LockedNode.jsx',
].map(read).join('\n');
for (const label of ['Курс · развилка', 'Code test', 'Картинки · код', 'Закрытое продолжение', 'LOCK']) {
  if (courseMapNodes.includes(label)) fail(`technical course-map label returned: ${label}`);
}

const forbiddenLearnerCopy = {
  'components/shell/RouteErrorBoundary.jsx': [
    'Остальная оболочка TaskForge продолжает работать',
  ],
  'features/assignment-solve/AssignmentSolveFeature.jsx': [
    'Runner принимает',
    'скрыта от ученика',
    'Открой «Редактировать»',
    'Для image-test доступны',
  ],
  'features/compiler/CompilerFeature.jsx': [
    'оболочке сервера',
    'отдельная среда фронтенд-задач',
    'Черновик сохраняется автоматически',
  ],
  'pages/MathTaskSolve.jsx': [
    'Здесь можно строить решения',
  ],
  'pages/TaskTestSolve.jsx': [
    'Чтобы начать попытку',
  ],
  'pages/minecraft/MinecraftChatPage.jsx': [
    'Компактная лента в стиле',
  ],
};
for (const [relativePath, phrases] of Object.entries(forbiddenLearnerCopy)) {
  const text = read(relativePath);
  for (const phrase of phrases) {
    if (text.includes(phrase)) {
      fail(`developer commentary leaked into learner UI: src/${relativePath} contains "${phrase}"`);
    }
  }
}

console.log('Frontend architecture invariants OK');
