const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..', 'src');

function fail(message) {
  console.error(`CT frontend architecture invariant failed: ${message}`);
  process.exit(1);
}

function walk(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const fullPath = path.join(directory, entry.name);
    return entry.isDirectory() ? walk(fullPath) : [fullPath];
  });
}

for (const relative of [
  'app/AppProviders.jsx',
  'app/AppShell.jsx',
  'components/shell/AppHeader.jsx',
  'components/shell/PageContent.jsx',
]) {
  if (!fs.existsSync(path.join(root, relative))) fail(`missing src/${relative}`);
}

if (fs.existsSync(path.join(root, 'components', 'Layout.jsx'))) {
  fail('legacy page-owned Layout.jsx returned');
}

for (const file of walk(path.join(root, 'pages')).filter((entry) => entry.endsWith('.jsx'))) {
  const text = fs.readFileSync(file, 'utf8');
  if (/components\/Layout|<Layout\b|<\/Layout>/.test(text)) {
    fail(`page still owns the shell: src/${path.relative(root, file)}`);
  }
}

const app = fs.readFileSync(path.join(root, 'App.jsx'), 'utf8');
if (!/lazy\(\(\) => import\(/.test(app)) fail('route-level lazy chunks are missing');
if (!/<Route element={<AppShell \/>}>/.test(app)) fail('persistent AppShell route is missing');


const forbiddenLearnerCopy = {
  'pages/CoursesHomePage.jsx': [
    'Сначала выбираем обычный курс',
    'Логика входа',
  ],
  'pages/CtTrainerPage.jsx': [
    'Backend недоступен',
    'fallback-версия',
    'Показана fallback',
    'JSON-блоки',
  ],
  'pages/LearningHomePage.jsx': [
    'Старые программные курсы',
    'Логика',
  ],
  'pages/LearningCoursePage.jsx': [
    'quiz-task-service',
    'Задания появляются внутри конкретных разделов',
  ],
  'pages/MainCoursePage.jsx': [
    'seed learning-content-service',
    'Здесь будет ЦТ/ЦЭ',
  ],
  'pages/SimpleHomePage.jsx': [
    'номер → HTML-конспект',
  ],
  'pages/SimpleSectionPage.jsx': [
    'Сначала чаще выпадают новые',
    'HTML-конспект',
  ],
  'components/RichConspectRenderer.jsx': [
    'HTML-конспект',
    'Конспект можно открыть на весь экран',
  ],
  'utils/ctCourseAdmin.js': [
    'Служебный корень для второго фронта',
    'HTML-конспект и задания',
  ],
  'components/CtStructureBootstrapPanel.jsx': [
    'Служебный корень для второго фронта',
    'HTML-конспект и задания',
  ],
};
for (const [relativePath, phrases] of Object.entries(forbiddenLearnerCopy)) {
  const fullPath = path.join(root, relativePath);
  if (!fs.existsSync(fullPath)) fail(`missing src/${relativePath}`);
  const text = fs.readFileSync(fullPath, 'utf8');
  for (const phrase of phrases) {
    if (text.includes(phrase)) {
      fail(`developer commentary leaked into learner UI: src/${relativePath} contains "${phrase}"`);
    }
  }
}

console.log('CT frontend architecture invariants OK');
