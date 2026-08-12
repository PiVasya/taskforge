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
  'features/course-assignments/courseMapDebug.js',
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
const appHeader = read('components/shell/AppHeader.jsx');
if (!/shell-quick-menu/.test(appHeader) || !/to="\/settings" className="btn-ghost w-full justify-start xl:hidden"/.test(appHeader)) {
  fail('desktop quick actions can duplicate the primary Settings action again');
}
const desktopSidebar = read('components/shell/DesktopSidebar.jsx');
if (!/createPortal/.test(desktopSidebar) || !/side-nav-rail-tooltip/.test(desktopSidebar) || !/title=\{rail \? undefined : title\}/.test(desktopSidebar)) {
  fail('collapsed desktop navigation lost its visible custom tooltip');
}
const neoBrutalCss = read('styles/neobrutal.css');
if (!/shell-quick-menu \.btn-ghost:hover/.test(neoBrutalCss) || !/settings-nav-item/.test(neoBrutalCss)) {
  fail('neo-brutal shell/settings interaction contrast overrides are missing');
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

if (!/activeViewRef/.test(courseFlowEditor) || !/modeTransitionRef/.test(courseFlowEditor) || !/loadedViewRef/.test(courseFlowEditor)) {
  fail('course map no longer isolates learner/editor lifecycle by view key');
}
if (!/SYNTHETIC_GUARD/.test(courseFlowEditor) || !/hasLearnerSyntheticArtifacts/.test(courseFlowEditor)) {
  fail('editor course map lost the learner synthetic-node guard');
}
if (/loadedRootRef/.test(courseFlowEditor)) {
  fail('course map load identity regressed to root-only state and can mix learner/editor graphs');
}
if (!/EDITOR_TREE_JOIN/.test(courseFlowEditor) || !/editorTreeFetchRef/.test(courseFlowEditor)) {
  fail('editor assignment-tree requests are no longer coalesced');
}
if (!/\[TFDBG MAP\]/.test(read('features/course-assignments/courseMapDebug.js'))) {
  fail('course-map console diagnostics prefix is missing');
}
if (!/useNodesInitialized/.test(courseFlowEditor) || !/course-map-node-layout-pending/.test(courseFlowEditor) || !/LAYOUT_INCREMENT_READY/.test(courseFlowEditor)) {
  fail('course map can render nodes before React Flow has applied their saved positions');
}
const courseMapCss = read('features/course-assignments/course-map.css');
if (!/course-map-node-layout-pending/.test(courseMapCss) || !/is-layout-pending/.test(courseMapCss)) {
  fail('course-map pending-layout visibility guard is missing');
}
const learnerAssignmentNodes = [
  'features/course-assignments/nodes/CodeTestNode.jsx',
  'features/course-assignments/nodes/TestNode.jsx',
  'features/course-assignments/nodes/ImageCodeNode.jsx',
  'features/course-assignments/nodes/MathNode.jsx',
].map(read).join('\n');
if (!/activateCourseMapAssignmentNode/.test(learnerAssignmentNodes) || !/role=\{data\?\.editorMode \? undefined : ["']link["']\}/.test(learnerAssignmentNodes)) {
  fail('learner course-map assignment cards no longer open directly while editor selection remains isolated');
}
if (!/course-map-node--locked:hover/.test(courseMapCss) || !/cursor:\s*not-allowed/.test(courseMapCss)) {
  fail('locked course-map placeholders can look interactive again');
}
if (/\.react-flow__node\.course-map-node-revealed\s*\{/.test(courseMapCss) || !/course-map-node-revealed > \.course-map-node/.test(courseMapCss)) {
  fail('course-map reveal animation must not overwrite the React Flow node wrapper transform');
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
if (!/CACHE_SCHEMA = 5/.test(courseMapCache) || !/sourceMode/.test(courseMapCache)) {
  fail('course-map local cache does not separate learner/editor source modes or invalidate polluted v4 entries');
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
const courseEditPage = read('pages/CourseEditPage.jsx');
if (!/visibilityMode/.test(courseEditPage) || !/value: 'public'/.test(courseEditPage) || !/value: 'groups'/.test(courseEditPage) || !/value: 'hidden'/.test(courseEditPage)) {
  fail('course audience regressed from the single public/groups/hidden selector');
}
const taskGraphJson = read('features/course-assignments/courseTaskGraphJson.js');
const taskGraphImport = read('features/course-assignments/courseTaskGraphImport.js');
const taskGraphDialog = read('features/course-assignments/components/JsonTaskGraphDialog.jsx');
const taskGraphDiff = read('features/course-assignments/components/JsonTaskGraphDiffModal.jsx');
if (!/TASK_GRAPH_SCHEMA_VERSION = 4/.test(taskGraphJson) || !/['"]courses['"]/.test(taskGraphJson) || !/['"]course['"]/.test(taskGraphJson)) {
  fail('canonical JSON graph v4 lost nested-course references');
}
if (!/includeLayout/.test(taskGraphDialog) || !/includeIds/.test(taskGraphDialog) || !/updateLayout/.test(taskGraphDiff) || !/updateConnections/.test(taskGraphDiff)) {
  fail('selective JSON import/export controls are missing');
}
if (!/createPortal/.test(taskGraphDialog) || !/100dvh/.test(taskGraphDialog) || !/min-h-0 flex-1 overflow-y-auto/.test(taskGraphDialog)) {
  fail('JSON graph editor must stay inside the viewport and scroll internally');
}
if (!/createPortal/.test(taskGraphDiff) || !/100dvh/.test(taskGraphDiff) || !/min-h-0 flex-1 overflow-y-auto/.test(taskGraphDiff)) {
  fail('JSON import preview must stay inside the viewport and scroll internally');
}
if (!/descriptor\.kind === 'course'/.test(taskGraphImport) || !/layoutPosition\(taskGraph, ref\)/.test(taskGraphImport)) {
  fail('task-graph import no longer applies layout/topology to nested course nodes');
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
