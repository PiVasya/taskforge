import { projectTaskGraphImport } from './courseTaskGraphImport';

export const TASK_GRAPH_SCHEMA_VERSION = 5;
export const TASK_GRAPH_LEGACY_SCHEMA_VERSION = 3;
export const TASK_GRAPH_PREVIOUS_SCHEMA_VERSION = 4;
export const TASK_GRAPH_FORMAT = 'taskforge-task-graph';
export const TASK_GRAPH_COURSE_REF = '$course';
export const TASK_GRAPH_MAX_TASKS = 5000;
export const TASK_GRAPH_MAX_CONNECTIONS = 20_000;

export const FULL_JSON_EXPORT_OPTIONS = Object.freeze({
  includeIds: true,
  includeContent: true,
  includeChecks: true,
  includeVisibility: true,
  includeConnections: true,
  includeConnectionAccess: true,
  includeLayout: true,
  includeGuide: true,
});

export const DEFAULT_JSON_IMPORT_OPTIONS = Object.freeze({
  updateContent: true,
  updateChecks: true,
  updateVisibility: true,
  updateConnections: true,
  updateConnectionAccess: true,
  updateLayout: true,
  allowDeletes: false,
  allowConnectionDeletes: false,
  allowDuplicates: false,
  connectionMode: 'merge',
});

const TASK_FIELDS = new Set([
  'key', 'id', 'course', 'type', 'title', 'description', 'language', 'allowedLanguages', 'tags',
  'rating', 'starterCode', 'testCases', 'testSettings', 'questions', 'blocks',
  'codeForbiddenCalls', 'codeRequiredCalls', 'isVisible', 'imageTestReferenceKey',
  'imageTestSimilarityThreshold', 'sql',
]);
const TOP_LEVEL_FIELDS = new Set(['schemaVersion', 'format', 'scopes', 'guide', 'courses', 'tasks', 'deleteTasks', 'connections', 'layout', 'datasets']);
const COURSE_FIELDS = new Set(['key', 'id', 'title', 'isPublic', 'isHiddenFromStudents', 'visibleGroupIds']);
const CONNECTION_FIELDS = new Set(['from', 'to', 'access']);
const ACCESS_FIELDS = new Set(['hidden', 'sequential']);
const LEGACY_LAYOUT_FIELDS = new Set([
  'nodes', 'edges', 'position', 'positionabsolute', 'x', 'y', 'coordinates', 'mapposition', 'nodeid',
]);
export const TASK_GRAPH_SCOPES = ['ids', 'content', 'checks', 'visibility', 'connections', 'connectionAccess', 'layout'];
const EFFECT_VALUES = new Set(['inherit', 'start', 'stop']);
const ZERO_GUID = '00000000-0000-0000-0000-000000000000';
const TINY_PNG_BASE64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=';

const codeTask = {
  key: 'input-output',
  type: 'code-test',
  title: 'Ввод и вывод',
  description: 'Считайте два целых числа и выведите их сумму.',
  language: 'cpp',
  allowedLanguages: ['cpp', 'python', 'csharp', 'onec'],
  starterCode: '#include <iostream>\nusing namespace std;\n\nint main()\n{\n    return 0;\n}\n',
  testCases: [
    { input: '2 4', expectedOutput: '6', isHidden: false },
    { input: '-5 12', expectedOutput: '7', isHidden: false },
    { input: '100 250', expectedOutput: '350', isHidden: true },
  ],
  codeRequiredCalls: ['cin', 'cout'],
  codeForbiddenCalls: ['system', 'exec', 'fork'],
  rating: 1,
  tags: 'ввод, вывод',
  isVisible: true,
};

const testTask = {
  key: 'json-basics',
  type: 'test',
  title: 'Все виды вопросов теста',
  description: 'Ответьте на вопросы о формате JSON.',
  testSettings: {
    maxAttempts: 3,
    unlimitedAttempts: false,
    passPercent: 75,
    shuffleQuestions: true,
    shuffleAnswers: true,
    allowReview: true,
    attemptTimeLimitsSeconds: [600, 480, 360],
  },
  questions: [
    {
      order: 0,
      type: 'single-choice',
      prompt: 'Какой тип JSON хранит true и false?',
      options: [
        { key: 'a', text: 'string' },
        { key: 'b', text: 'boolean' },
        { key: 'c', text: 'array' },
      ],
      correctOptionKeys: ['b'],
      acceptedAnswers: [],
      caseSensitive: false,
      trim: true,
    },
    {
      order: 1,
      type: 'multi-choice',
      prompt: 'Какие значения являются допустимыми JSON-значениями?',
      options: [
        { key: 'a', text: 'null' },
        { key: 'b', text: 'число' },
        { key: 'c', text: 'функция' },
        { key: 'd', text: 'массив' },
      ],
      correctOptionKeys: ['a', 'b', 'd'],
      acceptedAnswers: [],
      caseSensitive: false,
      trim: true,
    },
    {
      order: 2,
      type: 'fill',
      prompt: 'Дополните: расширение файла без точки — ____',
      options: [],
      correctOptionKeys: [],
      acceptedAnswers: ['json'],
      caseSensitive: false,
      trim: true,
    },
    {
      order: 3,
      type: 'text',
      prompt: 'Как называется пара из имени и значения внутри объекта?',
      options: [],
      correctOptionKeys: [],
      acceptedAnswers: ['свойство', 'поле', 'property'],
      caseSensitive: false,
      trim: true,
    },
  ],
  rating: 1,
  tags: 'json, тест',
  isVisible: true,
};

const imageTask = {
  key: 'draw-pixel',
  type: 'image-test',
  title: 'Изображение из кода',
  description: 'Создайте изображение 64 × 64 с чёрной точкой в центре.',
  language: 'python',
  allowedLanguages: ['python'],
  starterCode: 'from PIL import Image, ImageDraw\n\nimage = Image.new("RGB", (64, 64), "white")\ndraw = ImageDraw.Draw(image)\n',
  imageTestSimilarityThreshold: 92,
  testCases: [
    {
      input: '',
      expectedOutput: '',
      isHidden: false,
      threshold: 92,
      expectedImageBase64: TINY_PNG_BASE64,
      expectedImageContentType: 'image/png',
      expectedImageFileName: 'reference.png',
    },
  ],
  codeRequiredCalls: ['Image.new', 'save'],
  codeForbiddenCalls: ['open', 'requests', 'subprocess'],
  rating: 2,
  tags: 'графика, изображение',
  isVisible: true,
};

const mathTask = {
  key: 'math-blocks',
  type: 'math',
  title: 'Все виды математических блоков',
  description: 'Пройдите последовательность математических блоков.',
  testSettings: {
    maxAttempts: 3,
    unlimitedAttempts: false,
    passPercent: 80,
    shuffleBlocks: false,
    allowReview: true,
    attemptTimeLimitsSeconds: [900, 720, 600],
  },
  blocks: [
    {
      order: 0,
      kind: 'info',
      prompt: 'Используйте данные из условия в следующих блоках.',
      score: 0,
      isRequired: true,
    },
    {
      order: 1,
      kind: 'single-choice',
      prompt: 'Какое число является простым?',
      score: 1,
      isRequired: true,
      options: [
        { key: 'a', text: '9' },
        { key: 'b', text: '11' },
        { key: 'c', text: '15' },
      ],
      correctOptionKeys: ['b'],
    },
    {
      order: 2,
      kind: 'multi-choice',
      prompt: 'Выберите чётные числа.',
      score: 2,
      isRequired: true,
      options: [
        { key: 'a', text: '2' },
        { key: 'b', text: '3' },
        { key: 'c', text: '4' },
        { key: 'd', text: '7' },
      ],
      correctOptionKeys: ['a', 'c'],
    },
    {
      order: 3,
      kind: 'number',
      prompt: 'Введите число π с точностью до сотых.',
      score: 2,
      isRequired: true,
      acceptedAnswers: ['3.14'],
      numericTolerance: 0.01,
      caseSensitive: false,
      trim: true,
    },
    {
      order: 4,
      kind: 'expression',
      prompt: 'Разложите x² − 1 на множители.',
      score: 2,
      isRequired: true,
      acceptedAnswers: ['(x-1)(x+1)', '(x+1)(x-1)'],
      caseSensitive: false,
      trim: true,
    },
    {
      order: 5,
      kind: 'set',
      prompt: 'Запишите множество корней уравнения x³ − x = 0.',
      score: 2,
      isRequired: true,
      acceptedAnswers: ['-1,0,1', '{-1,0,1}'],
      caseSensitive: false,
      trim: true,
    },
    {
      order: 6,
      kind: 'order',
      prompt: 'Расположите этапы решения по порядку.',
      score: 2,
      isRequired: true,
      orderItems: ['Раскрыть скобки', 'Перенести слагаемые', 'Привести подобные', 'Найти неизвестное'],
    },
    {
      order: 7,
      kind: 'match',
      prompt: 'Сопоставьте выражение и результат.',
      score: 3,
      isRequired: true,
      matchLeftItems: [
        { key: 'l1', text: '2 + 3' },
        { key: 'l2', text: '3 × 4' },
        { key: 'l3', text: '2³' },
      ],
      matchRightItems: [
        { key: 'r1', text: '5' },
        { key: 'r2', text: '12' },
        { key: 'r3', text: '8' },
      ],
      matchPairs: [
        { leftKey: 'l1', rightKey: 'r1' },
        { leftKey: 'l2', rightKey: 'r2' },
        { leftKey: 'l3', rightKey: 'r3' },
      ],
    },
  ],
  rating: 2,
  tags: 'математика, блоки',
  isVisible: true,
};

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function simpleCodeTask(key, title, description = 'Решите задание и выведите ответ.') {
  return {
    key,
    type: 'code-test',
    title,
    description,
    language: 'cpp',
    allowedLanguages: ['cpp', 'python', 'csharp', 'onec'],
    starterCode: '',
    testCases: [
      { input: '1', expectedOutput: '1', isHidden: false },
      { input: '10', expectedOutput: '10', isHidden: true },
    ],
    codeRequiredCalls: [],
    codeForbiddenCalls: ['system', 'exec', 'fork'],
    rating: 1,
    tags: 'практика',
    isVisible: true,
  };
}

function simpleTestTask(key, title) {
  return {
    key,
    type: 'test',
    title,
    description: 'Выберите правильный ответ.',
    testSettings: {
      maxAttempts: 2,
      unlimitedAttempts: false,
      passPercent: 100,
      shuffleQuestions: false,
      shuffleAnswers: false,
      allowReview: true,
      attemptTimeLimitsSeconds: [],
    },
    questions: [
      {
        order: 0,
        type: 'single-choice',
        prompt: 'Продолжить путь?',
        options: [
          { key: 'a', text: 'Да' },
          { key: 'b', text: 'Нет' },
        ],
        correctOptionKeys: ['a'],
        acceptedAnswers: [],
        caseSensitive: false,
        trim: true,
      },
    ],
    rating: 1,
    tags: 'проверка',
    isVisible: true,
  };
}

function simpleMathTask(key, title) {
  return {
    key,
    type: 'math',
    title,
    description: 'Введите результат вычисления.',
    testSettings: {
      maxAttempts: 2,
      unlimitedAttempts: false,
      passPercent: 100,
      shuffleBlocks: false,
      allowReview: true,
      attemptTimeLimitsSeconds: [],
    },
    blocks: [
      {
        order: 0,
        kind: 'number',
        prompt: '2 + 2 =',
        score: 1,
        isRequired: true,
        acceptedAnswers: ['4'],
        numericTolerance: 0,
        caseSensitive: false,
        trim: true,
      },
    ],
    rating: 1,
    tags: 'математика',
    isVisible: true,
  };
}

export const TASK_GRAPH_MEGA_EXAMPLE = {
  schemaVersion: TASK_GRAPH_SCHEMA_VERSION,
  datasets: [],
  format: TASK_GRAPH_FORMAT,
  scopes: [...TASK_GRAPH_SCOPES],
  courses: [
    { key: 'course-advanced', title: 'Углубление' },
  ],
  tasks: [
    clone(codeTask),
    clone(testTask),
    clone(imageTask),
    { ...clone(mathTask), course: 'course-advanced' },
    simpleCodeTask('sequential-a', 'По одному · шаг 1'),
    simpleTestTask('sequential-b', 'По одному · шаг 2'),
    simpleMathTask('hidden-a', 'Скрытый участок · шаг 1'),
    simpleCodeTask('hidden-b', 'Скрытый участок · шаг 2'),
    simpleTestTask('combined-a', 'Скрытие + по одному · шаг 1'),
    simpleMathTask('combined-b', 'Скрытие + по одному · шаг 2'),
    simpleTestTask('merge-finish', 'Слияние всех ветвей'),
    simpleCodeTask('final-task', 'Финальное задание'),
    { ...simpleTestTask('unplaced-draft', 'Черновик вне карты'), isVisible: false },
  ],
  deleteTasks: [],
  connections: [
    { from: TASK_GRAPH_COURSE_REF, to: 'input-output' },
    { from: 'input-output', to: 'json-basics' },

    { from: 'json-basics', to: 'draw-pixel' },
    { from: 'draw-pixel', to: 'merge-finish' },

    { from: 'json-basics', to: 'course-advanced' },
    { from: 'course-advanced', to: 'math-blocks' },
    { from: 'math-blocks', to: 'merge-finish' },

    { from: 'json-basics', to: 'sequential-a', access: { sequential: 'start' } },
    { from: 'sequential-a', to: 'sequential-b' },
    { from: 'sequential-b', to: 'merge-finish', access: { sequential: 'stop' } },

    { from: 'json-basics', to: 'hidden-a', access: { hidden: 'start' } },
    { from: 'hidden-a', to: 'hidden-b' },
    { from: 'hidden-b', to: 'merge-finish', access: { hidden: 'stop' } },

    { from: 'json-basics', to: 'combined-a', access: { hidden: 'start', sequential: 'start' } },
    { from: 'combined-a', to: 'combined-b' },
    { from: 'combined-b', to: 'merge-finish', access: { hidden: 'stop', sequential: 'stop' } },

    { from: 'merge-finish', to: 'final-task' },
  ],
  layout: {
    viewport: { x: 136, y: 96, zoom: 0.9 },
    positions: {
      [TASK_GRAPH_COURSE_REF]: { x: 0, y: 0 },
      'input-output': { x: 578, y: 0 },
      'json-basics': { x: 1156, y: 0 },
      'draw-pixel': { x: 1734, y: -528 },
      'course-advanced': { x: 1734, y: -176 },
      'math-blocks': { x: 2312, y: -176 },
      'sequential-a': { x: 1734, y: 176 },
      'hidden-a': { x: 1734, y: 528 },
      'combined-a': { x: 1734, y: 880 },
      'merge-finish': { x: 2890, y: 0 },
      'final-task': { x: 3468, y: 0 },
    },
  },
};

const SIMPLE_CHAIN_EXAMPLE = {
  schemaVersion: TASK_GRAPH_SCHEMA_VERSION,
  datasets: [],
  format: TASK_GRAPH_FORMAT,
  scopes: ['content', 'checks', 'visibility', 'connections', 'connectionAccess'],
  courses: [],
  tasks: [
    simpleCodeTask('first', 'Первое задание'),
    simpleCodeTask('second', 'Второе задание'),
    simpleCodeTask('third', 'Третье задание'),
  ],
  connections: [
    { from: TASK_GRAPH_COURSE_REF, to: 'first' },
    { from: 'first', to: 'second' },
    { from: 'second', to: 'third' },
  ],
};

const BRANCH_EXAMPLE = {
  schemaVersion: TASK_GRAPH_SCHEMA_VERSION,
  datasets: [],
  format: TASK_GRAPH_FORMAT,
  scopes: ['content', 'checks', 'visibility', 'connections', 'connectionAccess'],
  courses: [],
  tasks: [
    simpleTestTask('start', 'Развилка'),
    simpleMathTask('left', 'Левая ветка'),
    simpleCodeTask('right', 'Правая ветка'),
    simpleTestTask('finish', 'Общий финал'),
  ],
  connections: [
    { from: TASK_GRAPH_COURSE_REF, to: 'start' },
    { from: 'start', to: 'left' },
    { from: 'start', to: 'right' },
    { from: 'left', to: 'finish' },
    { from: 'right', to: 'finish' },
  ],
};

const PROGRESSION_EXAMPLE = {
  schemaVersion: TASK_GRAPH_SCHEMA_VERSION,
  datasets: [],
  format: TASK_GRAPH_FORMAT,
  scopes: ['content', 'checks', 'visibility', 'connections', 'connectionAccess'],
  courses: [],
  tasks: [
    simpleCodeTask('gate', 'Входное задание'),
    simpleTestTask('hidden-step-1', 'Скрытый шаг 1'),
    simpleMathTask('hidden-step-2', 'Скрытый шаг 2'),
    simpleCodeTask('open-tail', 'Открытый хвост'),
  ],
  connections: [
    { from: TASK_GRAPH_COURSE_REF, to: 'gate' },
    { from: 'gate', to: 'hidden-step-1', access: { hidden: 'start', sequential: 'start' } },
    { from: 'hidden-step-1', to: 'hidden-step-2' },
    { from: 'hidden-step-2', to: 'open-tail', access: { hidden: 'stop', sequential: 'stop' } },
  ],
};

const DELETE_TASK_EXAMPLE = {
  schemaVersion: TASK_GRAPH_SCHEMA_VERSION,
  datasets: [],
  format: TASK_GRAPH_FORMAT,
  scopes: ['ids'],
  courses: [],
  tasks: [],
  deleteTasks: ['11111111-1111-4111-8111-111111111111'],
  connections: [],
};

export const TASK_GRAPH_EXAMPLES = [
  { key: 'mega', title: 'Мега-пример', type: 'вся схема', payload: TASK_GRAPH_MEGA_EXAMPLE },
  { key: 'chain', title: 'Цепочка', type: 'линейный путь', payload: SIMPLE_CHAIN_EXAMPLE },
  { key: 'branch', title: 'Развилка и слияние', type: 'ветви', payload: BRANCH_EXAMPLE },
  { key: 'progression', title: 'Скрытие и по одному', type: 'доступ', payload: PROGRESSION_EXAMPLE },
  { key: 'delete-task', title: 'Удаление задания', type: 'deleteTasks', payload: DELETE_TASK_EXAMPLE },
];

export const TASK_GRAPH_BEHAVIOR_GUIDE = {
  "identity": "key — ссылка только внутри одного файла, не постоянный идентификатор. Совпадение key или названия не объединяет сущности. Существующий UUID в поддереве выбирает сущность; свободный UUID создаёт новую с этим UUID; без id создаётся новая с UUID от сервера. Запускать новую операцию импорта без id в тот же курс — значит создавать новые задания и курсы, а не обновлять старые.",
  "foreignId": "UUID, занятый заданием или курсом вне импортируемого поддерева, отклоняется. Preview проверяет занятость на сервере, но не резервирует UUID: проверка повторяется при Apply.",
  "courseMetadata": "У существующего вложенного курса поля title, isPublic, isHiddenFromStudents и visibleGroupIds используются как справочная информация и не обновляют курс. Меняйте их в редакторе курса. В courses эти поля задают параметры только нового курса. Новый вложенный курс создаётся прямым дочерним курсом открытого курса; стрелки карты не меняют иерархию хранения.",
  "parent": "Для нового задания отсутствие course означает $course. Для существующего задания отсутствие course сохраняет фактический курс. Явно указанный course должен совпадать с его текущим курсом: перенос через JSON не поддерживается.",
  "scopes": "scopes задаёт присутствующие категории, а переключатели импорта разрешают их применение. Выключенная категория сохраняется у существующих заданий. Новому заданию нужны scope content и полные поля его типа; выключение обновления существующих заданий не превращает новую задачу в пустую ссылку. guide — справка, не данные курса.",
  "optionalArrays": "tasks всегда является массивом, в том числе []. courses можно не указывать. connections требуется, когда включено изменение связей или эффектов стрелок. Если обе категории выключены, поле connections можно опустить; его отсутствие не очищает карту. datasets можно опустить без SQL-ресурсов. Для sql со ссылкой на датасет этот ресурс должен быть объявлен в datasets[]. Отдельного scope datasets нет.",
  "versions": "Поддерживаются schemaVersion 3, 4 и 5; новые экспорты используют 5. Пустой datasets: [] допустим в версиях 3 и 4 без SQL. Непустые SQL-ресурсы требуют версии 5. В версии 3 отсутствующий scopes включает исторический набор категорий; явно переданный scopes учитывается. layout поддерживается с версии 4.",
  "replaceConnections": "Режим «Заменить связи перечисленных узлов» не означает append. Список connections задаёт целевые стрелки между перечисленными в JSON узлами и $course. Прочие стрелки сохраняются, пока их узлы остаются на карте. Перечисленный узел без связей и применяемой позиции снимается с карты вместе со всеми стрелками, но сама сущность не удаляется. Поэтому при частичном JSON смотрите точный список удалений в Preview.",
  "mergeConnections": "Для добавления ветки выберите режим «Добавить связи, сохранив существующие». Совпадающая пара from/to не дублируется. Старые узлы и связи сохраняются; эффекты совпавшей стрелки меняются только при включённой категории эффектов. Цикл с сохраняемыми связями тоже запрещён.",
  "emptyConnections": "connections: [] — явно пустой список. При выключенных связях он не меняет топологию, кроме удаления стрелок явно удаляемых заданий. В режиме добавления он не добавляет стрелок. В режиме замены он может снять перечисленные узлы без позиций с карты и удалить затронутые связи; это показывается в Preview и требует подтверждения.",
  "unplaced": "«Не на карте» не означает удаление задания или неуспешное сохранение его содержимого. Новый элемент без применяемой связи и без явной применяемой позиции сохраняется в этом списке намеренно. Связанные новые элементы без layout размещаются автоматически. Счётчик Preview относится к итоговой карте с учётом переключателей, а не только к входному JSON.",
  "layout": "layout.positions — частичное обновление: меняются только перечисленные позиции. layout.viewport задаёт x, y, zoom рабочей области. Если layout выключен, существующие координаты и viewport сохраняются, а новые связанные узлы получают свободные позиции. Явные координаты принимаются как авторский выбор; их взаимное наложение не устраняется принудительно.",
  "edgeEffects": "connectionAccess независим от connections. При экспорте эффектов без топологии массив connections остаётся как адресация существующих стрелок, но scope connections отсутствует. Такой импорт меняет эффекты, а не добавляет и не удаляет стрелки. При выключенных эффектах прежние настройки совпадающих связей сохраняются.",
  "datasetReuse": "SQL-датасеты переиспользуются по содержимому схемы, seed и engineOverrides, а не по key или имени. Переиспользование возможно только для доступного редактору, неархивного датасета; сохраняются его прежние имя и идентичность. Новое имя само по себе не создаёт копию. Изменение содержимого создаёт отдельный ресурс. Preview показывает решение «создать» или «использовать существующий».",
  "export": "Для файла, который можно импортировать обратно, оставьте ID заданий либо «Условия и настройки». Экспорт без обоих разделов заблокирован. Без ID задачи импортируются как новые; перед созданием возможных дублей нужно подтверждение. ID вложенных курсов экспортируются независимо от переключателя «ID заданий»: этот переключатель не означает клонирование всего дерева.",
  "noStoredMap": "Экспорт отражает сохранённую карту. Если документа карты ещё нет, экспорт не придумывает связи по порядку заданий; connections остаётся пустым, layout отсутствует. Сохраните нужную карту перед экспортом.",
  "completion": "Импорт завершён только после подтверждённой записи данных и карты. Курсы, задания и карта сохраняются этапами: общей транзакции между сервисами нет. При сбое часть объектов может уже существовать. Журнал сохраняет этап и UUID; продолжайте эту же операцию, а не создавайте новую.",
  "deletion": "Отсутствие задания в tasks не удаляет его из курса. Удаление задаётся только deleteTasks с UUID, scope ids и отдельным подтверждением. При удалении исчезают и стрелки этого задания даже с выключенной категорией связей. SQL-задание с историей проверок физически не удаляется; используйте isVisible=false.",
  "operationIdentity": "operationId обозначает одну операцию, а не задание. Повтор с тем же operationId и теми же JSON/настройками возвращает сохранённый результат, не создаёт дубли. Другой JSON с этим ключом отклоняется. Клиент создаёт ключ сам; не добавляйте operationId в JSON графа. В HTTP API он передаётся рядом с graph и options.",
  "resume": "После таймаута или перезагрузки откройте журнал в том же курсе и аккаунте. Проверка состояния ничего не записывает. Продолжение выполняется только по нажатию. В localStorage хранится только operationId, не условия или ответы тестов.",
  "mapConflict": "Если карту изменили после Preview, она не перезаписывается молча. Пересчитайте карту и подтвердите новый Preview. Если ответ сохранения потерян, сначала повторяется точно тот же этап: новый граф не подставляется по догадке.",
  "audit": "Отчёт JSON содержит этап, UUID, изменённые поля, хеши до/после и разницу карты. Исходный JSON, ответы тестов и SQL-скрипты в отчёт не включаются. requestSha256 относится к нормализованному graph и options, не к байтам исходного файла. Порядок массивов и содержимое строк учитываются.",
  "stop": "«Прекратить без отката» не удаляет уже созданные объекты и не восстанавливает удалённые. Нужно явное подтверждение. Если результат записи карты неизвестен, сначала нужно получить её подтверждённый результат."
};

export const TASK_GRAPH_GUIDE_SECTIONS = [
  { key: 'behavior', title: 'Как читать результат импорта', items: Object.entries(TASK_GRAPH_BEHAVIOR_GUIDE).map(([field, text]) => ({ field, text })) },
  {
    key: 'document',
    title: 'Документ',
    items: [
      { field: 'schemaVersion', text: `Актуальная версия — ${TASK_GRAPH_SCHEMA_VERSION}. Импорт версии ${TASK_GRAPH_LEGACY_SCHEMA_VERSION} поддерживается для совместимости.` },
      { field: 'format', text: `Всегда "${TASK_GRAPH_FORMAT}".` },
      { field: 'scopes', text: 'Показывает, какие разделы реально присутствуют в файле: ids, content, checks, visibility, connections, connectionAccess, layout.' },
      { field: 'courses', text: 'Вложенные course-ноды. Существующий id привязывает существующий курс; свободный UUID создаёт новый курс с этим id; без id TaskForge создаёт курс и генерирует UUID.' },
      { field: 'tasks', text: 'Задания текущего курса и его подкурсов. Поле course показывает, в каком курсе лежит задание. Порядок массива не задаёт порядок прохождения.' },
      { field: 'deleteTasks', text: 'Явный список UUID заданий для удаления. Отсутствие задания в tasks никогда не означает удаление.' },
      { field: 'connections', text: 'Направленные связи между заданиями и course-нодами. Они задают цепочки, развилки, слияния и эффекты стрелок.' },
      { field: 'layout', text: 'Отдельный раздел координат и viewport. Он не смешивается с содержимым заданий.' },
    ],
  },
  {
    key: 'scopes',
    title: 'Разделы экспорта',
    items: [
      { field: 'ids', text: 'id заданий. Существующий id обновляет конкретное задание, а свободный UUID может быть сохранён за новым заданием.' },
      { field: 'content', text: 'Тип, название, условие, язык, теги, рейтинг и стартовый код.' },
      { field: 'checks', text: 'Тест-кейсы, правильные ответы, настройки тестов и math-блоков, обязательные и запрещённые вызовы.' },
      { field: 'visibility', text: 'isVisible задания.' },
      { field: 'connections', text: 'Кто за кем идёт: from → to.' },
      { field: 'connectionAccess', text: 'Настройки скрытия и пошагового открытия на стрелках.' },
      { field: 'layout', text: 'Позиции нод и viewport карты.' },
    ],
  },
  {
    key: 'identity',
    title: 'Создание и обновление',
    items: [
      { field: 'key', text: 'Уникальный ключ элемента внутри JSON. По нему connections и layout ссылаются на задания и вложенные курсы.' },
      { field: 'id', text: 'Для заданий и вложенных курсов действует одинаковая идея: существующий id в текущем поддереве привязывает сущность; свободный UUID создаёт новую сущность с этим UUID; без id UUID генерирует TaskForge; чужой занятый UUID отклоняется.' },
      { field: 'course', text: `Для задания: ${TASK_GRAPH_COURSE_REF} или key вложенного курса из courses. Существующее задание JSON не переносит между курсами.` },
      { field: 'без id', text: 'Новая задача или новый вложенный курс могут быть без id — TaskForge сам сгенерирует UUID. Для нового курса обязателен title; для новой задачи нужны полноценные поля её типа.' },
      { field: TASK_GRAPH_COURSE_REF, text: 'Ссылка на открытую ноду курса. Может быть источником connection и ключом позиции в layout.positions.' },
    ],
  },
  {
    key: 'deletions',
    title: 'Удаление заданий',
    items: [
      { field: 'deleteTasks', text: `Массив UUID в schemaVersion ${TASK_GRAPH_SCHEMA_VERSION}. Удаляются только id, перечисленные здесь явно.` },
      { field: 'scope ids', text: 'Если используется deleteTasks, в scopes должен присутствовать ids.' },
      { field: 'подтверждение', text: 'Даже корректный deleteTasks не выполняется автоматически: в проверке импорта нужно отдельно разрешить удаление.' },
      { field: 'безопасность', text: 'Нельзя удалить задание из чужого поддерева или одновременно обновлять и удалять один id.' },
      { field: 'SQL', text: 'SQL-задание с сохранённой revision history физически не удаляется. Его нужно скрыть через isVisible=false.' },
      { field: 'повторный импорт', text: 'Если id уже отсутствует, повторный импорт считает его уже удалённым и не падает.' },
    ],
  },
  {
    key: 'connections',
    title: 'Пути',
    items: [
      { field: 'from / to', text: `from — ${TASK_GRAPH_COURSE_REF}, key вложенного курса или key задания; to — key вложенного курса или задания.` },
      { field: 'развилка', text: 'Несколько connections с одинаковым from.' },
      { field: 'слияние', text: 'Несколько connections с одинаковым to.' },
      { field: 'конец пути', text: 'Нет исходящей связи.' },
      { field: 'цикл', text: 'Запрещён. Карта остаётся DAG.' },
    ],
  },
  {
    key: 'access',
    title: 'Эффекты стрелок',
    items: [
      { field: 'access.hidden', text: 'start — начать полное скрытие участка; stop — закончить; inherit или отсутствие — наследовать.' },
      { field: 'access.sequential', text: 'start — начать открытие по одному заданию; stop — закончить; inherit или отсутствие — наследовать.' },
      { field: 'комбинация', text: 'hidden и sequential независимы и могут пересекаться.' },
    ],
  },
  {
    key: 'layout',
    title: 'Расположение',
    items: [
      { field: 'layout.positions', text: `Объект вида key -> {x,y}. Можно переставлять задания и вложенные course-ноды; для текущей корневой ноды используется "${TASK_GRAPH_COURSE_REF}".` },
      { field: 'layout.viewport', text: 'x, y и zoom рабочей области.' },
      { field: 'частичный layout', text: 'Можно указать позиции только тех нод, которые нужно переставить. Остальные остаются на месте.' },
      { field: 'важно', text: 'Координаты разрешены только внутри layout. position/x/y внутри tasks или connections запрещены.' },
    ],
  },
  {
    key: 'content',
    title: 'Содержимое задания',
    items: [
      { field: 'type', text: 'code-test, image-test, test, math, sql-test.' },
      { field: 'title / description', text: 'Название и условие. В description не помещается эталонное решение.' },
      { field: 'language / allowedLanguages / starterCode', text: 'Настройки кода и стартовый шаблон.' },
      { field: 'rating / tags', text: 'Рейтинг и теги.' },
    ],
  },
  {
    key: 'checks',
    title: 'Проверки и ответы',
    items: [
      { field: 'code-test / image-test', text: 'testCases, codeRequiredCalls, codeForbiddenCalls; для image-test также эталон и threshold.' },
      { field: 'test', text: 'testSettings и questions; unlimitedAttempts=true включает бесконечные попытки; типы single-choice, multi-choice, fill, text.' },
      { field: 'math', text: 'testSettings и blocks; unlimitedAttempts=true включает бесконечные попытки; виды info, single-choice, multi-choice, number, expression, set, order, match.' },
    ],
  },
  {
    key: 'import',
    title: 'Выбор при импорте',
    items: [
      { field: 'Содержимое', text: 'Разрешает менять название, условие, тип, языки, теги, рейтинг и стартовый код существующих заданий.' },
      { field: 'Проверки', text: 'Разрешает менять тесты, ответы, testSettings/math blocks и ограничения кода.' },
      { field: 'Видимость', text: 'Разрешает менять isVisible.' },
      { field: 'Связи', text: 'Разрешает менять топологию from → to для перечисленных заданий.' },
      { field: 'Эффекты связей', text: 'Разрешает менять hidden/sequential. Можно менять эффекты без перестройки топологии.' },
      { field: 'Позиции', text: 'Разрешает применять layout.positions и viewport. Если выключено, существующая раскладка остаётся.' },
      { field: 'Удаления', text: 'Отдельное опасное разрешение. Оно выключено по умолчанию и требуется только если JSON содержит deleteTasks.' },
      { field: 'id не равно перезаписать всё', text: 'id только выбирает существующее задание. Реально изменяются только включённые категории.' },
    ],
  },
];

export const TASK_GRAPH_AI_PROMPT = `Правила импорта:
${Object.values(TASK_GRAPH_BEHAVIOR_GUIDE).join('\n')}

Ты работаешь с JSON-графом курса TaskForge.

Верни только один валидный JSON без Markdown и текста вокруг него.

Актуальная оболочка:
- schemaVersion: ${TASK_GRAPH_SCHEMA_VERSION}
- format: "${TASK_GRAPH_FORMAT}"
- scopes: какие разделы действительно присутствуют
- courses: вложенные course-ноды: существующие и новые
- tasks: задания текущего курса и подкурсов
- deleteTasks: явный список UUID заданий для удаления; отсутствие задания в tasks ничего не удаляет
- connections: направленные связи
- layout: позиции и viewport, только если они нужны

Scopes:
- ids — id заданий: существующие для обновления, свободные UUID допустимы для детерминированного создания
- content — содержимое и основные настройки
- checks — тесты, ответы и проверки
- visibility — isVisible
- connections — топология from -> to
- connectionAccess — hidden/sequential на стрелках
- layout — координаты нод и viewport

Правила:
1. key обязателен и уникален среди courses/tasks. Он используется в connections и layout.positions.
2. courses содержит вложенные курсы: для существующего сохраняй реальный id; для нового можно указать свободный UUID или не указывать id. Без id TaskForge создаст UUID сам.
3. У каждого задания поле course — "${TASK_GRAPH_COURSE_REF}" или key вложенного курса. Новый вложенный курс создаётся непосредственным дочерним курсом импортируемого корня.
4. id существующей задачи/курса сохраняй. Для новой задачи или нового курса можно не указывать id либо указать свободный UUID для детерминированного создания.
5. Наличие id само по себе не означает, что TaskForge перезапишет всё: пользователь отдельно выбирает, какие категории разрешено импортировать.
6. Текущий курс обозначается "${TASK_GRAPH_COURSE_REF}".
7. Порядок прохождения задаётся connections. Не используй sort.
8. Несколько связей из одной ноды создают развилку; несколько входящих — слияние. Циклы запрещены.
9. access.hidden и access.sequential принимают start, stop, inherit.
10. Допустимые типы: code-test, image-test, test, math, sql-test.
11. Не добавляй analyticsSettings и не помещай правильное решение в description.
12. Расположение задаётся только как layout.positions[key] = {x,y}; viewport — layout.viewport = {x,y,zoom}.
13. Никогда не помещай position, x, y, nodes, edges или nodeId внутрь tasks/connections.
14. Если пользователь просит только переставить карту, сохрани ids/keys/connections и измени только layout; не переписывай условия и тесты.
15. Если пользователь просит изменить только задания, layout можно не трогать.
16. Частичный layout допустим: неуказанные позиции TaskForge оставит прежними.
17. Не больше ${TASK_GRAPH_MAX_TASKS} заданий и ${TASK_GRAPH_MAX_CONNECTIONS} связей.
18. codeRequiredCalls/codeForbiddenCalls — только учебные требования. Если конкретный синтаксис обязателен (например, while, do, &&, break), прямо напиши это в цели/условии задания.
19. Не фиксируй одну синтаксическую форму, если по смыслу допустимы эквивалентные варианты: for/while, &&/вложенный if, float(x)/map(float, ...). Массив codeRequiredCalls имеет семантику AND, а не «любой из вариантов».
20. Поле guide — документация. Не переноси примеры x/y из guide.layout в реальные поля; импорт должен игнорировать guide.
21. Удаляй задания только в schemaVersion 5 через deleteTasks: ["uuid", ...]. Никогда не считай отсутствие задания в tasks командой удаления.
22. Один id нельзя одновременно оставлять в tasks и добавлять в deleteTasks. Для deleteTasks обязательно сохрани scope ids.
23. SQL-задания с сохранённой revision history не удаляй через deleteTasks; если их нужно убрать для учеников, ставь isVisible=false.

Мега-пример:
${JSON.stringify(TASK_GRAPH_MEGA_EXAMPLE, null, 2)}

Задача пользователя:
`;

function cleanId(value) {
  const text = String(value || '').trim();
  if (!text) return '';
  if (isGuid(text)) {
    const normalized = text.toLowerCase();
    return normalized === ZERO_GUID ? '' : normalized;
  }
  return text;
}

function isGuid(value) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(String(value || '').trim());
}

function effectValue(value) {
  const normalized = String(value || 'inherit').trim().toLowerCase();
  return EFFECT_VALUES.has(normalized) ? normalized : 'inherit';
}

function normalizeAccess(value) {
  const access = value && typeof value === 'object' && !Array.isArray(value) ? value : {};
  return {
    hidden: effectValue(access.hidden),
    sequential: effectValue(access.sequential),
  };
}

function isPlainObject(value) {
  return Boolean(value && typeof value === 'object' && !Array.isArray(value));
}

function collectLegacyLayoutIssues(value, path, issues) {
  if (path === '$.datasets' || /^\$\.tasks\[\d+\]\.sql$/.test(path)) return;
  if (Array.isArray(value)) {
    value.forEach((item, index) => collectLegacyLayoutIssues(item, `${path}[${index}]`, issues));
    return;
  }
  if (!isPlainObject(value)) return;
  for (const [key, item] of Object.entries(value)) {
    const itemPath = `${path}.${key}`;
    // `guide` is exported documentation for humans/AI. It may intentionally contain
    // example x/y coordinates and must never be validated as live course layout.
    if (path === '$' && (key === 'layout' || key === 'guide')) continue;
    if (LEGACY_LAYOUT_FIELDS.has(String(key).toLowerCase())) {
      issues.push({ path: itemPath, message: 'Координаты разрешены только внутри layout.positions.' });
      continue;
    }
    collectLegacyLayoutIssues(item, itemPath, issues);
  }
}

function validateLayout(layout, keySet, issues) {
  if (layout === undefined || layout === null) return;
  if (!isPlainObject(layout)) {
    issues.push({ path: '$.layout', message: 'layout должен быть объектом.' });
    return;
  }
  for (const key of Object.keys(layout)) {
    if (!['viewport', 'positions'].includes(key)) issues.push({ path: `$.layout.${key}`, message: 'Неизвестное поле layout.' });
  }
  if (layout.viewport !== undefined) {
    if (!isPlainObject(layout.viewport)) issues.push({ path: '$.layout.viewport', message: 'viewport должен быть объектом.' });
    else {
      for (const key of Object.keys(layout.viewport)) if (!['x', 'y', 'zoom'].includes(key)) issues.push({ path: `$.layout.viewport.${key}`, message: 'Неизвестное поле viewport.' });
      for (const key of ['x', 'y']) if (!Number.isFinite(Number(layout.viewport[key]))) issues.push({ path: `$.layout.viewport.${key}`, message: 'Укажите число.' });
      if (layout.viewport.zoom !== undefined && (!Number.isFinite(Number(layout.viewport.zoom)) || Number(layout.viewport.zoom) < 0.05 || Number(layout.viewport.zoom) > 4)) {
        issues.push({ path: '$.layout.viewport.zoom', message: 'zoom должен быть от 0.05 до 4.' });
      }
    }
  }
  if (layout.positions !== undefined) {
    if (!isPlainObject(layout.positions)) issues.push({ path: '$.layout.positions', message: 'positions должен быть объектом key -> {x,y}.' });
    else {
      for (const [ref, position] of Object.entries(layout.positions)) {
        const path = `$.layout.positions.${ref}`;
        if (ref !== TASK_GRAPH_COURSE_REF && !keySet.has(ref)) issues.push({ path, message: `Неизвестный key позиции "${ref}".` });
        if (!isPlainObject(position)) { issues.push({ path, message: 'Позиция должна быть объектом {x,y}.' }); continue; }
        for (const key of Object.keys(position)) if (!['x', 'y'].includes(key)) issues.push({ path: `${path}.${key}`, message: 'Неизвестное поле позиции.' });
        if (!Number.isFinite(Number(position.x))) issues.push({ path: `${path}.x`, message: 'Укажите число.' });
        if (!Number.isFinite(Number(position.y))) issues.push({ path: `${path}.y`, message: 'Укажите число.' });
      }
    }
  }
}

function getLegacyAssignments(parsed) {
  if (Array.isArray(parsed)) return parsed;
  if (!parsed || typeof parsed !== 'object') return [];
  for (const key of ['assignments', 'items', 'tasks']) {
    if (Array.isArray(parsed[key])) return parsed[key];
  }
  if (parsed.title || parsed.type || parsed.assignmentTitle) return [parsed];
  return [];
}

export function isCanonicalTaskGraph(parsed) {
  return Boolean(parsed && typeof parsed === 'object' && !Array.isArray(parsed)
    && Array.isArray(parsed.tasks)
    && (parsed.format === TASK_GRAPH_FORMAT
      || [TASK_GRAPH_LEGACY_SCHEMA_VERSION, TASK_GRAPH_PREVIOUS_SCHEMA_VERSION, TASK_GRAPH_SCHEMA_VERSION].includes(Number(parsed.schemaVersion))
      || Array.isArray(parsed.connections)));
}

function validateAttemptSettings(value, path, issues) {
  if (value === undefined) return;
  if (!isPlainObject(value)) {
    issues.push({ path, message: 'Ожидался объект настроек.' });
    return;
  }
  if (value.unlimitedAttempts !== undefined && typeof value.unlimitedAttempts !== 'boolean') {
    issues.push({ path: `${path}.unlimitedAttempts`, message: 'unlimitedAttempts должен быть true или false.' });
  }
  if (value.maxAttempts !== undefined && (!Number.isInteger(Number(value.maxAttempts)) || Number(value.maxAttempts) < 1)) {
    issues.push({ path: `${path}.maxAttempts`, message: 'maxAttempts должен быть целым числом не меньше 1.' });
  }
  if (value.passPercent !== undefined && (!Number.isFinite(Number(value.passPercent)) || Number(value.passPercent) < 0 || Number(value.passPercent) > 100)) {
    issues.push({ path: `${path}.passPercent`, message: 'passPercent должен быть от 0 до 100.' });
  }
  if (value.attemptTimeLimitsSeconds !== undefined) {
    if (!Array.isArray(value.attemptTimeLimitsSeconds)) {
      issues.push({ path: `${path}.attemptTimeLimitsSeconds`, message: 'Ожидался массив лимитов времени.' });
    } else {
      value.attemptTimeLimitsSeconds.forEach((item, index) => {
        if (item !== null && (!Number.isInteger(Number(item)) || Number(item) <= 0)) {
          issues.push({ path: `${path}.attemptTimeLimitsSeconds[${index}]`, message: 'Укажите положительное число секунд или null.' });
        }
      });
    }
  }
}

function validateTestCases(value, path, issues, { required = false, image = false, hasTopReference = false } = {}) {
  if (value === undefined) {
    if (required) issues.push({ path, message: 'Нужен непустой testCases.' });
    return;
  }
  if (!Array.isArray(value)) {
    issues.push({ path, message: 'testCases должен быть массивом.' });
    return;
  }
  if (required && value.length === 0) issues.push({ path, message: 'Нужен непустой testCases.' });
  value.forEach((testCase, index) => {
    const itemPath = `${path}[${index}]`;
    if (!isPlainObject(testCase)) {
      issues.push({ path: itemPath, message: 'Ожидался объект тест-кейса.' });
      return;
    }
    if (testCase.threshold !== undefined && (!Number.isFinite(Number(testCase.threshold)) || Number(testCase.threshold) < 0 || Number(testCase.threshold) > 100)) {
      issues.push({ path: `${itemPath}.threshold`, message: 'threshold должен быть от 0 до 100.' });
    }
    if (image) {
      const hasReference = hasTopReference
        || Boolean(String(testCase.expectedImageBase64 || '').trim())
        || Boolean(String(testCase.expectedImageKey || '').trim())
        || Boolean(String(testCase.expectedImageUrl || '').trim());
      if (!hasReference) issues.push({ path: itemPath, message: 'Для image-test укажите эталон изображения.' });
    }
  });
}

function validateQuestions(value, path, issues, required = false) {
  if (value === undefined) {
    if (required) issues.push({ path, message: 'Нужен непустой questions.' });
    return;
  }
  if (!Array.isArray(value)) {
    issues.push({ path, message: 'questions должен быть массивом.' });
    return;
  }
  if (required && value.length === 0) issues.push({ path, message: 'Нужен непустой questions.' });
  const allowed = new Set(['single-choice', 'multi-choice', 'fill', 'text']);
  value.forEach((question, index) => {
    const itemPath = `${path}[${index}]`;
    if (!isPlainObject(question)) {
      issues.push({ path: itemPath, message: 'Ожидался объект вопроса.' });
      return;
    }
    const type = String(question.type || 'single-choice').trim().toLowerCase();
    if (!allowed.has(type)) issues.push({ path: `${itemPath}.type`, message: 'Допустимы single-choice, multi-choice, fill и text.' });
    if (!String(question.prompt || '').trim()) issues.push({ path: `${itemPath}.prompt`, message: 'Укажите текст вопроса.' });
    if (type === 'single-choice' || type === 'multi-choice') {
      const options = Array.isArray(question.options) ? question.options.filter((item) => String(item?.text || '').trim()) : [];
      const optionKeys = new Set(options.map((item) => String(item?.key || '').trim()).filter(Boolean));
      const correct = Array.isArray(question.correctOptionKeys) ? question.correctOptionKeys.map(String).filter(Boolean) : [];
      if (options.length < 2) issues.push({ path: `${itemPath}.options`, message: 'Нужно минимум два варианта ответа.' });
      if (!correct.length) issues.push({ path: `${itemPath}.correctOptionKeys`, message: 'Укажите правильный вариант.' });
      if (correct.some((key) => !optionKeys.has(key))) issues.push({ path: `${itemPath}.correctOptionKeys`, message: 'Правильный key должен существовать в options.' });
      if (type === 'single-choice' && correct.length > 1) issues.push({ path: `${itemPath}.correctOptionKeys`, message: 'Для single-choice нужен один правильный вариант.' });
    }
    if (type === 'fill' || type === 'text') {
      const accepted = Array.isArray(question.acceptedAnswers) ? question.acceptedAnswers.map((item) => String(item || '').trim()).filter(Boolean) : [];
      if (!accepted.length) issues.push({ path: `${itemPath}.acceptedAnswers`, message: 'Добавьте хотя бы один допустимый ответ.' });
    }
  });
}

function validateMathBlocks(value, path, issues, required = false) {
  if (value === undefined) {
    if (required) issues.push({ path, message: 'Нужен непустой blocks.' });
    return;
  }
  if (!Array.isArray(value)) {
    issues.push({ path, message: 'blocks должен быть массивом.' });
    return;
  }
  if (required && value.length === 0) issues.push({ path, message: 'Нужен непустой blocks.' });
  const allowed = new Set(['info', 'single-choice', 'multi-choice', 'number', 'expression', 'set', 'order', 'match']);
  value.forEach((block, index) => {
    const itemPath = `${path}[${index}]`;
    if (!isPlainObject(block)) {
      issues.push({ path: itemPath, message: 'Ожидался объект math-блока.' });
      return;
    }
    const kind = String(block.kind || 'info').trim().toLowerCase();
    if (!allowed.has(kind)) issues.push({ path: `${itemPath}.kind`, message: 'Неизвестный вид math-блока.' });
    if (!String(block.prompt || '').trim() && !String(block.promptContentJson || '').trim()) {
      issues.push({ path: `${itemPath}.prompt`, message: 'Укажите текст блока.' });
    }
    if (kind === 'single-choice' || kind === 'multi-choice') {
      const options = Array.isArray(block.options) ? block.options.filter((item) => String(item?.text || '').trim()) : [];
      const optionKeys = new Set(options.map((item) => String(item?.key || '').trim()).filter(Boolean));
      const correct = Array.isArray(block.correctOptionKeys) ? block.correctOptionKeys.map(String).filter(Boolean) : [];
      if (options.length < 2) issues.push({ path: `${itemPath}.options`, message: 'Нужно минимум два варианта ответа.' });
      if (!correct.length) issues.push({ path: `${itemPath}.correctOptionKeys`, message: 'Укажите правильный вариант.' });
      if (correct.some((key) => !optionKeys.has(key))) issues.push({ path: `${itemPath}.correctOptionKeys`, message: 'Правильный key должен существовать в options.' });
      if (kind === 'single-choice' && correct.length > 1) issues.push({ path: `${itemPath}.correctOptionKeys`, message: 'Для single-choice нужен один правильный вариант.' });
    }
    if (kind === 'number' || kind === 'expression' || kind === 'set') {
      const accepted = Array.isArray(block.acceptedAnswers) ? block.acceptedAnswers.map((item) => String(item || '').trim()).filter(Boolean) : [];
      if (!accepted.length) issues.push({ path: `${itemPath}.acceptedAnswers`, message: 'Добавьте хотя бы один допустимый ответ.' });
    }
    if (kind === 'number' && block.numericTolerance !== undefined && (!Number.isFinite(Number(block.numericTolerance)) || Number(block.numericTolerance) < 0)) {
      issues.push({ path: `${itemPath}.numericTolerance`, message: 'numericTolerance не может быть отрицательным.' });
    }
    if (kind === 'order') {
      const items = Array.isArray(block.orderItems) ? block.orderItems.map((item) => String(item || '').trim()).filter(Boolean) : [];
      if (items.length < 2) issues.push({ path: `${itemPath}.orderItems`, message: 'Нужно минимум два элемента.' });
    }
    if (kind === 'match') {
      const left = Array.isArray(block.matchLeftItems) ? block.matchLeftItems.filter((item) => String(item?.key || '').trim() && String(item?.text || '').trim()) : [];
      const right = Array.isArray(block.matchRightItems) ? block.matchRightItems.filter((item) => String(item?.key || '').trim() && String(item?.text || '').trim()) : [];
      const pairs = Array.isArray(block.matchPairs) ? block.matchPairs.filter((item) => String(item?.leftKey || '').trim() && String(item?.rightKey || '').trim()) : [];
      if (!left.length || !right.length || !pairs.length) issues.push({ path: itemPath, message: 'Заполните обе колонки и правильные пары.' });
    }
  });
}

function validateTaskPayload(task, index, isPatch) {
  const issues = [];
  const path = `$.tasks[${index}]`;
  if (!isPlainObject(task)) return [{ path, message: 'Ожидался объект задания.' }];
  for (const key of Object.keys(task)) {
    if (LEGACY_LAYOUT_FIELDS.has(String(key).toLowerCase())) continue;
    if (!TASK_FIELDS.has(key)) issues.push({ path: `${path}.${key}`, message: 'Неизвестное поле задания.' });
  }
  const title = String(task.title || '').trim();
  const type = String(task.type || '').trim().toLowerCase();
  if (!isPatch && !title) issues.push({ path: `${path}.title`, message: 'title обязателен для нового задания.' });
  if (title.length > 200) issues.push({ path: `${path}.title`, message: 'title не должен быть длиннее 200 символов.' });
  if (task.type !== undefined && !['code-test', 'image-test', 'test', 'math', 'sql-test'].includes(type)) {
    issues.push({ path: `${path}.type`, message: 'Допустимы code-test, image-test, test, math, sql-test.' });
  }
  if (task.rating !== undefined && (!Number.isFinite(Number(task.rating)) || Number(task.rating) < 0)) {
    issues.push({ path: `${path}.rating`, message: 'rating не может быть отрицательным.' });
  }
  if (task.allowedLanguages !== undefined && (!Array.isArray(task.allowedLanguages) || task.allowedLanguages.some((item) => typeof item !== 'string'))) {
    issues.push({ path: `${path}.allowedLanguages`, message: 'allowedLanguages должен быть массивом строк.' });
  }
  if (task.isVisible !== undefined && typeof task.isVisible !== 'boolean') {
    issues.push({ path: `${path}.isVisible`, message: 'isVisible должен быть true или false.' });
  }
  if (task.imageTestSimilarityThreshold !== undefined
    && (!Number.isFinite(Number(task.imageTestSimilarityThreshold)) || Number(task.imageTestSimilarityThreshold) < 0 || Number(task.imageTestSimilarityThreshold) > 100)) {
    issues.push({ path: `${path}.imageTestSimilarityThreshold`, message: 'Порог должен быть от 0 до 100.' });
  }

  const needsCodeCases = !isPatch && (type === 'code-test' || type === 'image-test');
  if (task.testCases !== undefined || needsCodeCases) {
    validateTestCases(task.testCases, `${path}.testCases`, issues, {
      required: needsCodeCases,
      image: type === 'image-test',
      hasTopReference: Boolean(String(task.imageTestReferenceKey || '').trim()),
    });
  }
  if (task.testSettings !== undefined) validateAttemptSettings(task.testSettings, `${path}.testSettings`, issues);
  if (task.questions !== undefined || (!isPatch && type === 'test')) {
    validateQuestions(task.questions, `${path}.questions`, issues, !isPatch && type === 'test');
  }
  if (task.blocks !== undefined || (!isPatch && type === 'math')) {
    validateMathBlocks(task.blocks, `${path}.blocks`, issues, !isPatch && type === 'math');
  }
  return issues;
}

function hasCycle(keys, connections) {
  const adjacency = new Map(keys.map((key) => [key, []]));
  const indegree = new Map(keys.map((key) => [key, 0]));
  for (const connection of connections) {
    if (connection.from === TASK_GRAPH_COURSE_REF) continue;
    if (!adjacency.has(connection.from) || !indegree.has(connection.to)) continue;
    adjacency.get(connection.from).push(connection.to);
    indegree.set(connection.to, indegree.get(connection.to) + 1);
  }
  const queue = [...indegree.entries()].filter(([, count]) => count === 0).map(([key]) => key);
  let cursor = 0;
  while (cursor < queue.length) {
    const key = queue[cursor++];
    for (const target of adjacency.get(key) || []) {
      indegree.set(target, indegree.get(target) - 1);
      if (indegree.get(target) === 0) queue.push(target);
    }
  }
  return queue.length !== keys.length;
}

export function normalizeTaskGraphPayload(parsed) {
  if (!isCanonicalTaskGraph(parsed)) {
    const assignments = getLegacyAssignments(parsed);
    return {
      legacy: true,
      schemaVersion: 2,
      format: 'legacy-assignment-list',
      courses: [],
      tasks: assignments.map((task, index) => ({ ...task, key: `task-${String(index + 1).padStart(3, '0')}` })),
      deleteTasks: [],
      connections: [],
    };
  }
  const schemaVersion = Number(parsed.schemaVersion);
  const scopes = schemaVersion === TASK_GRAPH_LEGACY_SCHEMA_VERSION && parsed.scopes === undefined
    ? ['ids', 'content', 'checks', 'visibility', 'connections', 'connectionAccess']
    : (Array.isArray(parsed.scopes) ? parsed.scopes.map(String) : []);
  return {
    legacy: false,
    schemaVersion,
    format: String(parsed.format || ''),
    datasets: Array.isArray(parsed.datasets) ? parsed.datasets : [],
    scopes,
    courses: Array.isArray(parsed.courses) ? parsed.courses : [],
    tasks: Array.isArray(parsed.tasks) ? parsed.tasks : [],
    deleteTasks: Array.isArray(parsed.deleteTasks) ? parsed.deleteTasks : (parsed.deleteTasks ?? []),
    connections: Array.isArray(parsed.connections) ? parsed.connections : [],
    layout: isPlainObject(parsed.layout) ? parsed.layout : (parsed.layout ?? null),
  };
}

export function validateTaskGraphPayload(parsed, importOptions = {}) {
  const graph = normalizeTaskGraphPayload(parsed);
  const issues = [];
  collectLegacyLayoutIssues(parsed, '$', issues);
  if (graph.legacy) {
    if (!graph.tasks.length) issues.push({ path: '$', message: 'JSON не содержит заданий.' });
    if (graph.tasks.length > TASK_GRAPH_MAX_TASKS) issues.push({ path: '$', message: `Не больше ${TASK_GRAPH_MAX_TASKS} заданий.` });
    graph.tasks.forEach((task, index) => {
      const path = `$.tasks[${index}]`;
      if (!task || typeof task !== 'object' || Array.isArray(task)) {
        issues.push({ path, message: 'Ожидался объект задания.' });
        return;
      }
      const id = cleanId(task.id ?? task.assignmentId);
      const title = String(task.title || task.assignmentTitle || task.name || '').trim();
      if (id && !isGuid(id)) issues.push({ path: `${path}.id`, message: 'id должен быть GUID существующего задания.' });
      if (!id && !title) issues.push({ path: `${path}.title`, message: 'Укажите название нового задания.' });
    });
    return { graph, issues, legacy: true };
  }

  const root = parsed;
  validateSqlResources(root, graph, issues);
  for (const key of Object.keys(root || {})) {
    if (LEGACY_LAYOUT_FIELDS.has(String(key).toLowerCase())) continue;
    if (!TOP_LEVEL_FIELDS.has(key)) issues.push({ path: `$.${key}`, message: 'Неизвестное поле верхнего уровня.' });
  }
  const schemaVersion = Number(root?.schemaVersion);
  if (![TASK_GRAPH_LEGACY_SCHEMA_VERSION, TASK_GRAPH_PREVIOUS_SCHEMA_VERSION, TASK_GRAPH_SCHEMA_VERSION].includes(schemaVersion)) issues.push({ path: '$.schemaVersion', message: `Поддерживаются ${TASK_GRAPH_LEGACY_SCHEMA_VERSION}, ${TASK_GRAPH_PREVIOUS_SCHEMA_VERSION} и ${TASK_GRAPH_SCHEMA_VERSION}.` });
  if (root?.format !== TASK_GRAPH_FORMAT) issues.push({ path: '$.format', message: `Ожидается "${TASK_GRAPH_FORMAT}".` });
  if (schemaVersion >= TASK_GRAPH_PREVIOUS_SCHEMA_VERSION || root?.scopes !== undefined) {
    if (!Array.isArray(root?.scopes)) issues.push({ path: '$.scopes', message: 'Нужен массив scopes.' });
    else {
      const seenScopes = new Set();
      root.scopes.forEach((scope, index) => {
        if (typeof scope !== 'string' || !TASK_GRAPH_SCOPES.includes(scope)) issues.push({ path: `$.scopes[${index}]`, message: 'Неизвестный scope.' });
        else if (seenScopes.has(scope)) issues.push({ path: `$.scopes[${index}]`, message: 'Scope указан повторно.' });
        else seenScopes.add(scope);
      });
    }
  }
  if (schemaVersion >= TASK_GRAPH_PREVIOUS_SCHEMA_VERSION && root?.layout != null && !graph.scopes.includes('layout')) {
    issues.push({ path: '$.scopes', message: 'Добавьте scope layout, если документ содержит layout.' });
  }
  if (!Array.isArray(root?.tasks)) issues.push({ path: '$.tasks', message: 'Нужен массив заданий.' });
  if (root?.deleteTasks !== undefined && root?.deleteTasks !== null && !Array.isArray(root.deleteTasks)) {
    issues.push({ path: '$.deleteTasks', message: 'deleteTasks должен быть массивом GUID заданий.' });
  }
  const needsConnections = (graph.scopes.includes('connections') && importOptions.updateConnections !== false)
    || (graph.scopes.includes('connectionAccess') && importOptions.updateConnectionAccess !== false);
  if (!Array.isArray(root?.connections) && (root?.connections !== undefined || needsConnections)) {
    issues.push({ path: '$.connections', message: 'Нужен массив связей для выбранного раздела. При выключенных связях и эффектах поле можно не указывать.' });
  }
  if (root?.courses !== undefined && !Array.isArray(root.courses)) issues.push({ path: '$.courses', message: 'courses должен быть массивом.' });

  if (graph.tasks.length > TASK_GRAPH_MAX_TASKS) issues.push({ path: '$.tasks', message: `Не больше ${TASK_GRAPH_MAX_TASKS} заданий.` });
  if (graph.connections.length > TASK_GRAPH_MAX_CONNECTIONS) issues.push({ path: '$.connections', message: `Не больше ${TASK_GRAPH_MAX_CONNECTIONS} связей.` });

  if (graph.courses.length > 1000) issues.push({ path: '$.courses', message: 'Не больше 1000 вложенных курсов.' });
  const courseKeySet = new Set();
  const courseIdSet = new Set();
  graph.courses.forEach((course, index) => {
    const path = `$.courses[${index}]`;
    if (!isPlainObject(course)) {
      issues.push({ path, message: 'Ожидался объект курса.' });
      return;
    }
    for (const field of Object.keys(course)) {
      if (LEGACY_LAYOUT_FIELDS.has(String(field).toLowerCase())) continue;
      if (!COURSE_FIELDS.has(field)) issues.push({ path: `${path}.${field}`, message: 'Неизвестное поле курса.' });
    }
    const key = String(course.key || '').trim();
    if (!key) issues.push({ path: `${path}.key`, message: 'Укажите уникальный key вложенного курса.' });
    else {
      if (key === TASK_GRAPH_COURSE_REF) issues.push({ path: `${path}.key`, message: `${TASK_GRAPH_COURSE_REF} зарезервирован.` });
      if (key.length > 80 || !/^[\p{L}\p{N}._-]+$/u.test(key)) issues.push({ path: `${path}.key`, message: 'До 80 букв, цифр и символов . _ -.' });
      if (courseKeySet.has(key)) issues.push({ path: `${path}.key`, message: `key "${key}" используется повторно.` });
      courseKeySet.add(key);
    }
    const id = cleanId(course.id);
    if (id && !isGuid(id)) issues.push({ path: `${path}.id`, message: 'Если id указан, он должен быть корректным GUID.' });
    else if (id && courseIdSet.has(id)) issues.push({ path: `${path}.id`, message: 'Один id вложенного курса нельзя объявлять дважды.' });
    else if (id) courseIdSet.add(id);
    if (!id && !String(course.title || '').trim()) issues.push({ path: `${path}.title`, message: 'Для нового курса без id укажите title.' });
  });

  const keySet = new Set();
  const idSet = new Set();
  const graphRefSet = new Set([TASK_GRAPH_COURSE_REF, ...courseKeySet]);
  graph.tasks.forEach((task, index) => {
    const path = `$.tasks[${index}]`;
    const key = String(task?.key || '').trim();
    if (!key) issues.push({ path: `${path}.key`, message: 'Укажите уникальный key.' });
    else {
      if (key === TASK_GRAPH_COURSE_REF) issues.push({ path: `${path}.key`, message: `${TASK_GRAPH_COURSE_REF} зарезервирован.` });
      if (key.length > 80 || !/^[\p{L}\p{N}._-]+$/u.test(key)) issues.push({ path: `${path}.key`, message: 'До 80 букв, цифр и символов . _ -.' });
      if (keySet.has(key) || courseKeySet.has(key)) issues.push({ path: `${path}.key`, message: `key "${key}" уже используется другим элементом графа.` });
      keySet.add(key);
      graphRefSet.add(key);
    }
    const courseRef = String(task?.course || TASK_GRAPH_COURSE_REF).trim();
    if (!graphRefSet.has(courseRef) || (courseRef !== TASK_GRAPH_COURSE_REF && !courseKeySet.has(courseRef))) {
      issues.push({ path: `${path}.course`, message: `Курс "${courseRef}" не объявлен в courses.` });
    }
    const id = cleanId(task?.id);
    if (id && !isGuid(id)) issues.push({ path: `${path}.id`, message: 'id должен быть GUID существующего задания.' });
    if (id && idSet.has(id)) issues.push({ path: `${path}.id`, message: 'Один id нельзя использовать дважды.' });
    if (id) idSet.add(id);
    issues.push(...validateTaskPayload(task, index, Boolean(id)));
  });

  const deleteIdSet = new Set();
  const deleteTasks = Array.isArray(graph.deleteTasks) ? graph.deleteTasks : [];
  deleteTasks.forEach((value, index) => {
    const path = `$.deleteTasks[${index}]`;
    if (typeof value !== 'string' || !isGuid(value) || !cleanId(value)) {
      issues.push({ path, message: 'Укажите корректный непустой GUID задания для удаления.' });
      return;
    }
    const id = cleanId(value);
    if (deleteIdSet.has(id)) issues.push({ path, message: 'Один id нельзя указывать в deleteTasks несколько раз.' });
    deleteIdSet.add(id);
    if (idSet.has(id)) issues.push({ path, message: 'Нельзя одновременно обновлять/создавать и удалять одно и то же задание.' });
  });
  if (deleteTasks.length > 0 && graph.schemaVersion !== TASK_GRAPH_SCHEMA_VERSION) {
    issues.push({ path: '$.schemaVersion', message: `deleteTasks поддерживается только в schemaVersion ${TASK_GRAPH_SCHEMA_VERSION}.` });
  }
  if (deleteTasks.length > 0 && !graph.scopes.includes('ids')) {
    issues.push({ path: '$.scopes', message: 'Добавьте scope ids, если JSON содержит deleteTasks.' });
  }
  if (graph.tasks.length + deleteTasks.length > TASK_GRAPH_MAX_TASKS) {
    issues.push({ path: '$', message: `За один импорт можно обработать не больше ${TASK_GRAPH_MAX_TASKS} заданий суммарно, включая deleteTasks.` });
  }

  validateLayout(graph.layout, graphRefSet, issues);

  const connectionSet = new Set();
  const normalizedConnections = [];
  graph.connections.forEach((connection, index) => {
    const path = `$.connections[${index}]`;
    if (!connection || typeof connection !== 'object' || Array.isArray(connection)) {
      issues.push({ path, message: 'Ожидался объект связи.' });
      return;
    }
    for (const key of Object.keys(connection)) {
      if (LEGACY_LAYOUT_FIELDS.has(String(key).toLowerCase())) continue;
      if (!CONNECTION_FIELDS.has(key)) issues.push({ path: `${path}.${key}`, message: 'Неизвестное поле связи.' });
    }
    const from = String(connection.from || '').trim();
    const to = String(connection.to || '').trim();
    if (!from) issues.push({ path: `${path}.from`, message: `Укажите ${TASK_GRAPH_COURSE_REF} или key задания.` });
    else if (!graphRefSet.has(from)) issues.push({ path: `${path}.from`, message: `Элемент "${from}" не объявлен в courses/tasks.` });
    if (!to) issues.push({ path: `${path}.to`, message: 'Укажите key следующего задания.' });
    else if (to === TASK_GRAPH_COURSE_REF) issues.push({ path: `${path}.to`, message: `${TASK_GRAPH_COURSE_REF} может быть только источником.` });
    else if (!graphRefSet.has(to)) issues.push({ path: `${path}.to`, message: `Элемент "${to}" не объявлен в courses/tasks.` });
    if (from && from === to) issues.push({ path, message: 'Задание нельзя соединить с самим собой.' });
    const signature = `${from}\u001f${to}`;
    if (from && to && connectionSet.has(signature)) issues.push({ path, message: 'Такая связь уже объявлена.' });
    if (from && to) connectionSet.add(signature);

    const access = connection.access;
    if (access !== undefined && access !== null) {
      if (!access || typeof access !== 'object' || Array.isArray(access)) issues.push({ path: `${path}.access`, message: 'access должен быть объектом.' });
      else {
        for (const key of Object.keys(access)) {
          if (LEGACY_LAYOUT_FIELDS.has(String(key).toLowerCase())) continue;
          if (!ACCESS_FIELDS.has(key)) issues.push({ path: `${path}.access.${key}`, message: 'Неизвестное поле эффекта.' });
        }
        for (const field of ['hidden', 'sequential']) {
          if (access[field] !== undefined && !EFFECT_VALUES.has(String(access[field]).toLowerCase())) {
            issues.push({ path: `${path}.access.${field}`, message: 'Допустимы start, stop и inherit.' });
          }
        }
      }
    }
    normalizedConnections.push({ from, to, access: normalizeAccess(access) });
  });

  if (hasCycle([...courseKeySet, ...keySet], normalizedConnections)) issues.push({ path: '$.connections', message: 'Связи не должны образовывать цикл.' });
  return { graph: { ...graph, connections: normalizedConnections }, issues, legacy: false };
}

function connectedTaskKeys(graph) {
  const keys = new Set();
  for (const connection of graph.connections || []) {
    if (connection?.from && connection.from !== TASK_GRAPH_COURSE_REF) keys.add(String(connection.from));
    if (connection?.to) keys.add(String(connection.to));
  }
  return keys;
}

export function summarizeTaskGraphPayload(parsed) {
  try {
    const { graph, issues, legacy } = validateTaskGraphPayload(parsed);
    const connected = connectedTaskKeys(graph);
    const unplaced = legacy ? 0 : graph.tasks.filter((task) => !connected.has(String(task?.key || ''))).length;
    const topology = legacy
      ? ' · без графа'
      : ` · ${graph.connections.length} связей${unplaced ? ` · в JSON без связей ${unplaced}` : ''}`;
    const deletions = Array.isArray(graph.deleteTasks) && graph.deleteTasks.length ? ` · удалить ${graph.deleteTasks.length}` : '';
    return `${graph.tasks.length} заданий${deletions}${graph.courses?.length ? ` · курсов ${graph.courses.length}` : ''}${topology}${issues.length ? ` · ошибок ${issues.length}` : ''}`;
  } catch {
    return 'JSON не читается';
  }
}

function normalizeComparable(value) {
  if (value === undefined) return undefined;
  if (Array.isArray(value)) return value.map(normalizeComparable);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, normalizeComparable(value[key])]));
  }
  return value;
}

function sameValue(left, right) {
  return JSON.stringify(normalizeComparable(left)) === JSON.stringify(normalizeComparable(right));
}

const DIFF_FIELDS = [
  ['title', 'Название'], ['type', 'Тип'], ['description', 'Описание'], ['language', 'Язык'],
  ['allowedLanguages', 'Доступные языки'], ['tags', 'Теги'], ['rating', 'Рейтинг'], ['starterCode', 'Стартовый код'], ['testCases', 'Тест-кейсы'],
  ['testSettings', 'Настройки попыток'], ['questions', 'Вопросы'], ['blocks', 'Math-блоки'],
  ['codeRequiredCalls', 'Обязательные вызовы'], ['codeForbiddenCalls', 'Запрещённые вызовы'],
  ['isVisible', 'Видимость'], ['imageTestReferenceKey', 'Эталон изображения'],
  ['imageTestSimilarityThreshold', 'Порог изображения'],
];

function connectionSignature(connection, identityByKey, includeAccess = true) {
  const access = normalizeAccess(connection.access);
  const from = connection.from === TASK_GRAPH_COURSE_REF ? TASK_GRAPH_COURSE_REF : (identityByKey.get(connection.from) || `key:${connection.from}`);
  const to = identityByKey.get(connection.to) || `key:${connection.to}`;
  return includeAccess
    ? `${from}\u001f${to}\u001f${access.hidden}\u001f${access.sequential}`
    : `${from}\u001f${to}`;
}

function graphIdentityByKey(graph, missingPrefix) {
  const result = new Map([[TASK_GRAPH_COURSE_REF, TASK_GRAPH_COURSE_REF]]);
  for (const course of graph?.courses || []) {
    const key = String(course?.key || '');
    if (!key) continue;
    const id = cleanId(course?.id);
    result.set(key, id ? `course:${id}` : `${missingPrefix}:course:${key}`);
  }
  for (const task of graph?.tasks || []) {
    const key = String(task?.key || '');
    if (!key) continue;
    const id = cleanId(task?.id ?? task?.assignmentId);
    result.set(key, id ? `task:${id}` : `${missingPrefix}:task:${key}`);
  }
  return result;
}

function graphTitleByKey(graph) {
  const result = new Map([[TASK_GRAPH_COURSE_REF, 'Текущий курс']]);
  for (const course of graph?.courses || []) {
    const key = String(course?.key || '');
    if (key) result.set(key, String(course?.title || key || 'Курс'));
  }
  for (const task of graph?.tasks || []) {
    const key = String(task?.key || '');
    if (key) result.set(key, String(task?.title || key || 'Задание'));
  }
  return result;
}

function connectionRow(connection, index, titleByKey, status) {
  const access = normalizeAccess(connection.access);
  const from = String(connection?.from || '');
  const to = String(connection?.to || '');
  return {
    index,
    status,
    from,
    to,
    fromLabel: from === TASK_GRAPH_COURSE_REF ? 'Текущий курс' : (titleByKey.get(from) || from),
    toLabel: titleByKey.get(to) || to,
    hidden: access.hidden,
    sequential: access.sequential,
  };
}

export function buildTaskGraphImportDiff(parsed, currentExport, importOptions = {}, preflight = {}) {
  const incomingValidation = validateTaskGraphPayload(parsed, importOptions);
  const currentValidation = validateTaskGraphPayload(currentExport);
  const incoming = incomingValidation.graph;
  const current = currentValidation.graph;
  const importIssues = [...(preflight.issues || [])];
  const scopes = new Set(incoming.scopes || []);
  const options = {
    updateContent: importOptions.updateContent !== false && scopes.has('content'),
    updateChecks: importOptions.updateChecks !== false && scopes.has('checks'),
    updateVisibility: importOptions.updateVisibility !== false && scopes.has('visibility'),
    updateConnections: importOptions.updateConnections !== false && scopes.has('connections'),
    updateConnectionAccess: importOptions.updateConnectionAccess !== false && scopes.has('connectionAccess'),
    updateLayout: importOptions.updateLayout !== false && scopes.has('layout'),
    connectionMode: importOptions.connectionMode === 'replace' ? 'replace' : 'merge',
    allowConnectionDeletes: importOptions.allowConnectionDeletes === true,
    allowDuplicates: importOptions.allowDuplicates === true,
    allowDeletes: importOptions.allowDeletes === true,
  };
  const currentById = new Map(current.tasks.map((task) => [cleanId(task.id), task]).filter(([id]) => id));
  const currentTitles = new Set(current.tasks.map((task) => String(task.title || '').trim().toLowerCase()).filter(Boolean));
  const incomingTitlesSeen = new Set();
  const incomingTitle = graphTitleByKey(incoming);
  const currentTitle = graphTitleByKey(current);

  const deleteRows = (Array.isArray(incoming.deleteTasks) ? incoming.deleteTasks : []).map((rawId, index) => {
    const id = cleanId(rawId);
    const existing = id ? currentById.get(id) : null;
    if (existing?.type === 'sql-test' && existing?.sql) {
      importIssues.push({
        path: `$.deleteTasks[${index}]`,
        message: 'SQL-задание с сохранённой историей нельзя удалить. Скройте его через isVisible=false.',
      });
    }
    const courseRef = String(existing?.course || TASK_GRAPH_COURSE_REF);
    return {
      index,
      id,
      title: String(existing?.title || id || `Удаление #${index + 1}`),
      type: String(existing?.type || ''),
      courseRef,
      courseLabel: currentTitle.get(courseRef) || courseRef,
      action: existing ? 'delete' : 'missing',
      issues: [],
    };
  });

  const rows = incoming.tasks.map((rawTask, index) => {
    const task = isPlainObject(rawTask) ? rawTask : {};
    const id = cleanId(task.id ?? task.assignmentId);
    const existing = id ? currentById.get(id) : null;
    const title = String(task.title || existing?.title || `Импорт #${index + 1}`).trim();
    const titleKey = title.toLowerCase();
    const repeatedTitleInBatch = Boolean(titleKey && incomingTitlesSeen.has(titleKey));
    const duplicateTitle = Boolean(!existing && titleKey && (currentTitles.has(titleKey) || repeatedTitleInBatch));
    if (titleKey) incomingTitlesSeen.add(titleKey);
    if (existing && task.course !== undefined) {
      const incomingCourse = incoming.courses.find((row) => row.key === task.course);
      const existingCourse = current.courses.find((row) => row.key === existing.course);
      const expectedId = task.course === TASK_GRAPH_COURSE_REF ? TASK_GRAPH_COURSE_REF : cleanId(incomingCourse?.id);
      const actualId = !existing.course || existing.course === TASK_GRAPH_COURSE_REF ? TASK_GRAPH_COURSE_REF : cleanId(existingCourse?.id);
      if (expectedId !== actualId) importIssues.push({ path: `$.tasks[${index}].course`, message: 'JSON-импорт не переносит существующее задание между курсами.' });
    }
    if (!existing && id && !String(task.title || '').trim()) importIssues.push({ path: `$.tasks[${index}].title`, message: 'Для нового задания нужен title, даже если указан свободный UUID.' });
    if (!existing && !scopes.has('content') && !incomingValidation.legacy) importIssues.push({ path: `$.tasks[${index}]`, message: 'Для нового задания нужны scope content и полное описание.' });

    if (existing && options.updateContent && task.type !== undefined) {
      const currentType = String(existing.type || '').trim().toLowerCase();
      const nextType = String(task.type || '').trim().toLowerCase();
      if (currentType && nextType && currentType !== nextType && (currentType === 'sql-test' || nextType === 'sql-test')) {
        importIssues.push({
          path: `$.tasks[${index}].type`,
          message: 'Тип существующего SQL-задания нельзя менять через импорт. Создайте новое задание с новым id.',
        });
      }
    }
    const changes = existing
      ? DIFF_FIELDS.map(([key, label]) => {
          const group = key === 'isVisible'
            ? 'visibility'
            : ['testCases', 'testSettings', 'questions', 'blocks', 'codeRequiredCalls', 'codeForbiddenCalls', 'imageTestReferenceKey', 'imageTestSimilarityThreshold'].includes(key)
              ? 'checks'
              : 'content';
          if (group === 'content' && !options.updateContent) return null;
          if (group === 'checks' && !options.updateChecks) return null;
          if (group === 'visibility' && !options.updateVisibility) return null;
          if (task[key] === undefined || sameValue(existing[key], task[key])) return null;
          return { key, label, before: existing[key], after: task[key] };
        }).filter(Boolean)
      : [];
    const action = existing ? (changes.length ? 'update' : 'unchanged') : 'create';
    const rowIssues = [...incomingValidation.issues, ...importIssues]
      .filter((issue) => issue.path === `$.tasks[${index}]` || issue.path.startsWith(`$.tasks[${index}].`));
    return {
      index,
      id,
      key: task.key,
      courseRef: String(existing?.course || task?.course || TASK_GRAPH_COURSE_REF),
      courseLabel: existing ? (currentTitle.get(String(existing.course || TASK_GRAPH_COURSE_REF)) || existing.course) : (incomingTitle.get(String(task?.course || TASK_GRAPH_COURSE_REF)) || String(task?.course || TASK_GRAPH_COURSE_REF)),
      title,
      type: task.type || existing?.type || 'code-test',
      action,
      duplicateTitle,
      repeatedTitleInBatch,
      changes,
      issues: rowIssues.map((issue) => issue.message),
    };
  });

  const incomingIdentity = graphIdentityByKey(incoming, 'new');
  const currentIdentity = graphIdentityByKey(current, 'current');
  const currentTopologyConnections = new Map(current.connections.map((connection, index) => [
    connectionSignature(connection, currentIdentity, false),
    { connection, index },
  ]));
  const projection = incomingValidation.legacy || incomingValidation.issues.length
    ? null : projectTaskGraphImport(incoming, current, options);
  if (projection?.error === 'cycle') importIssues.push({ path: '$.connections', message: 'Связи образуют цикл вместе с сохраняемой частью карты.' });
  const connectionAddedCount = projection?.added.length || 0;
  const connectionRemovedCount = projection?.removed.length || 0;
  const connectionUnchangedCount = projection?.kept.length ?? currentTopologyConnections.size;
  const connectionAccessChangedCount = projection?.accessChanged.length || 0;
  const unplacedCount = projection?.placements.filter((row) => !row.placed).length || 0;
  const connectionRows = incomingValidation.legacy || (!options.updateConnections && !options.updateConnectionAccess)
    ? []
    : incoming.connections.map((connection, index) => {
        const signature = connectionSignature(connection, incomingIdentity, false);
        const currentRow = currentTopologyConnections.get(signature);
        let status = 'unchanged';
        if (!currentRow) status = options.updateConnections ? 'add' : 'missing';
        else if (options.updateConnectionAccess
          && !sameValue(normalizeAccess(currentRow.connection.access), normalizeAccess(connection.access))) status = 'update';
        return connectionRow(connection, index, incomingTitle, status);
      });
  const removedConnectionRows = (projection?.removed || []).map((edge) => connectionRow(edge.original, edge.originalIndex, currentTitle, 'remove'));


  const currentCourseIds = new Set((current.courses || []).map((course) => cleanId(course?.id)).filter(Boolean));
  const courseCreateCount = (incoming.courses || []).filter((course) => {
    const id = cleanId(course?.id);
    return !id || !currentCourseIds.has(id);
  }).length;
  const courseExistingCount = Math.max(0, (incoming.courses || []).length - courseCreateCount);
  const existingCourseTitles = new Map();
  for (const course of current.courses || []) {
    const title = String(course.title || '').trim().toLowerCase();
    if (!title) continue;
    if (!existingCourseTitles.has(title)) existingCourseTitles.set(title, new Set());
    existingCourseTitles.get(title).add(cleanId(course.id));
  }
  const duplicateCourseTitles = (incoming.courses || []).filter((course) => {
    const title = String(course.title || '').trim().toLowerCase();
    const id = cleanId(course.id);
    return title && existingCourseTitles.has(title) && !existingCourseTitles.get(title).has(id);
  });
  const layoutPositionCount = options.updateLayout && incoming.layout?.positions && typeof incoming.layout.positions === 'object'
    ? Object.keys(incoming.layout.positions).length
    : 0;
  const warnings = [];
  const repeatedTitles = rows.filter((row) => row.repeatedTitleInBatch);
  if (duplicateCourseTitles.length) warnings.push(`В JSON есть ${duplicateCourseTitles.length} вложенных курсов с названиями существующих курсов, но другими UUID. Проверьте сопоставление перед созданием дублей.`);
  if (repeatedTitles.length) warnings.push(`В JSON повторяются названия у разных задач: ${repeatedTitles.map((row) => row.key).join(', ')}. Сверьте ключи и UUID: название не служит идентификатором.`);
  for (const course of incoming.courses || []) {
    const existing = current.courses.find((row) => cleanId(row.id) && cleanId(row.id) === cleanId(course.id));
    if (!existing) continue;
    const ignored = ['title', 'isPublic', 'isHiddenFromStudents', 'visibleGroupIds'].filter((field) => course[field] !== undefined && !sameValue(course[field], existing[field]));
    if (ignored.length) warnings.push(`Курс «${existing.title || course.key}»: ${ignored.join(', ')} не обновляются импортом. Эти поля используются только при создании курса; существующий курс изменяйте в его редакторе.`);
  }
  if (unplacedCount) warnings.push(`Вне карты после импорта: ${unplacedCount}. Эти элементы сохранятся в списке «Не на карте». Для размещения добавьте связь либо явную позицию с включённым layout.`);
  const operationCount = rows.filter((row) => row.action !== 'unchanged').length + deleteRows.filter((row) => row.action === 'delete').length
    + courseCreateCount + connectionAddedCount + connectionRemovedCount + connectionAccessChangedCount
    + (projection?.positionChanges || 0) + (projection?.viewportChanged ? 1 : 0);
  const hasImportWork = operationCount > 0 || (preflight.datasets || []).some((dataset) => dataset.action === 'create');

  // Reachability describes the effective map, including preserved edges in a
  // layout-only/merge import. Canonical keys may differ for the same UUID.
  const nodeRef = (key) => projection?.referenceNodes?.incoming?.[key] || key;
  const outgoing = new Map();
  const effectiveEdges = projection?.edges || (incoming.connections || []).map((edge) => ({ source: edge.from, target: edge.to }));
  for (const connection of effectiveEdges) {
    const from = String(connection.source || '');
    const to = String(connection.target || '');
    if (!outgoing.has(from)) outgoing.set(from, []);
    outgoing.get(from).push(to);
  }
  const rootNode = nodeRef(TASK_GRAPH_COURSE_REF);
  const rootReachable = new Set([rootNode]);
  const fromRoot = [rootNode];
  for (let index = 0; index < fromRoot.length; index += 1) {
    for (const to of outgoing.get(fromRoot[index]) || []) {
      if (rootReachable.has(to)) continue;
      rootReachable.add(to);
      fromRoot.push(to);
    }
  }
  const courseRows = (incoming.courses || []).map((course) => {
    const courseKey = String(course.key || '');
    const children = rows.filter((row) => {
      const existing = currentById.get(row.id);
      return existing ? currentIdentity.get(existing.course || TASK_GRAPH_COURSE_REF) === incomingIdentity.get(courseKey)
        : String(row.courseRef || '') === courseKey;
    });
    const childKeys = new Set(children.map((row) => nodeRef(row.key)));
    const courseNode = nodeRef(courseKey);
    const visited = new Set([courseNode]);
    const queue = [courseNode];
    for (let index = 0; index < queue.length; index += 1) {
      for (const to of outgoing.get(queue[index]) || []) {
        if (visited.has(to)) continue;
        visited.add(to);
        queue.push(to);
      }
    }
    const unreachableKeys = children.filter((row) => !visited.has(nodeRef(row.key))).map((row) => row.key);
    return {
      key: courseKey,
      id: course.id || null,
      title: course.title || courseKey,
      taskCount: children.length,
      reachableFromRoot: rootReachable.has(courseNode),
      createCount: children.filter((row) => row.action === 'create').length,
      existingCount: children.filter((row) => row.action !== 'create').length,
      directLinkCount: (outgoing.get(courseNode) || []).filter((to) => childKeys.has(to)).length,
      reachableCount: children.length - unreachableKeys.length,
      unreachableKeys,
    };
  });
  if (options.updateConnections) {
    const unreachableFromRoot = rows.filter((row) => !rootReachable.has(nodeRef(row.key)));
    if (unreachableFromRoot.length) warnings.push(`От корневого курса $course нет пути до ${unreachableFromRoot.length} заданий из JSON. Проверьте стрелки от корня и вложенных курсов.`);
    for (const course of courseRows) {
      if (!course.reachableFromRoot) warnings.push(`Курс «${course.title}» не достижим от $course; его счётчик может остаться нулевым.`);
      if (course.unreachableKeys.length) warnings.push(`Курс «${course.title}»: ${course.unreachableKeys.length} заданий в JSON не достижимы по стрелкам от этого курса. Проверьте связи по key перед импортом.`);
    }
  }
  return {
    total: operationCount,
    taskCount: rows.length,
    courseRows,
    warnings,
    placements: projection?.placements || [],
    previewMap: projection ? { nodes: projection.previewNodes || projection.nodes || [], edges: projection.edges || [] } : null,
    datasets: preflight.datasets || [],
    requiresConnectionDeleteConfirmation: connectionRemovedCount > 0 && !options.allowConnectionDeletes,
    requiresDuplicateConfirmation: (rows.some((row) => row.duplicateTitle) || duplicateCourseTitles.length > 0) && !options.allowDuplicates,

    courseCount: Array.isArray(incoming.courses) ? incoming.courses.length : 0,
    courseCreateCount,
    courseExistingCount,
    createCount: rows.filter((row) => row.action === 'create').length,
    updateCount: rows.filter((row) => row.action === 'update').length,
    unchangedCount: rows.filter((row) => row.action === 'unchanged').length,
    deleteRequestCount: deleteRows.length,
    deleteCount: deleteRows.filter((row) => row.action === 'delete').length,
    deleteMissingCount: deleteRows.filter((row) => row.action === 'missing').length,
    requiresDeleteConfirmation: deleteRows.some((row) => row.action === 'delete') && !options.allowDeletes,
    withoutIdCount: rows.filter((row) => !row.id).length,
    duplicateTitleCount: rows.filter((row) => row.duplicateTitle).length + duplicateCourseTitles.length,
    duplicateCourseTitleCount: duplicateCourseTitles.length,
    repeatedTitleInBatchCount: repeatedTitles.length,
    validationErrorCount: incomingValidation.issues.length + importIssues.length,
    graphIssues: [...incomingValidation.issues, ...importIssues],
    legacy: incomingValidation.legacy,
    scopes: incoming.scopes || [],
    layoutPositionCount,
    hasImportWork,
    importOptions: options,
    connectionCount: incomingValidation.legacy ? current.connections.length : incoming.connections.length,
    connectionAddedCount,
    connectionRemovedCount,
    connectionUnchangedCount,
    connectionAccessChangedCount,
    connectionRows,
    removedConnectionRows,
    unplacedCount,
    deleteRows: deleteRows.map((row) => ({
      ...row,
      issues: [...incomingValidation.issues, ...importIssues]
        .filter((issue) => issue.path === `$.deleteTasks[${row.index}]` || issue.path.startsWith(`$.deleteTasks[${row.index}].`))
        .map((issue) => issue.message),
    })),
    rows,
  };
}

export function shortTaskGraphValue(value) {
  if (value === undefined) return '—';
  if (value === null) return 'null';
  if (typeof value === 'string') return value.length > 500 ? `${value.slice(0, 500)}…` : value;
  const text = JSON.stringify(value, null, 2);
  return text.length > 1200 ? `${text.slice(0, 1200)}…` : text;
}

export function taskGraphExampleToText(example) {
  return JSON.stringify(example?.payload ?? example ?? TASK_GRAPH_MEGA_EXAMPLE, null, 2);
}

// Dedicated v5 documents are not legacy diagram coordinates or code-test JSON.
function validateSqlResources(root, graph, issues) {
  const problem = (path, message) => issues.push({ path, message });
  const hasSql = graph.tasks.some((task) => task?.sql || task?.type === 'sql-test');
  if (graph.schemaVersion < 5) {
    if (hasSql || (root.datasets !== undefined && (!Array.isArray(root.datasets) || root.datasets.length))) problem('$.datasets', 'SQL-ресурсы требуют schemaVersion 5. Пустой datasets: [] допустим без SQL.');
    return;
  }
  if (root.datasets !== undefined && !Array.isArray(root.datasets)) { problem('$.datasets', 'datasets должен быть массивом.'); return; }
  const datasets = root.datasets || [];
  if (datasets.length > 256) problem('$.datasets', 'Допускается не больше 256 SQL-датасетов.');
  const keys = new Set();
  datasets.forEach((d,i) => {
    const path = `$.datasets[${i}]`;
    if (!isPlainObject(d)) { problem(path,'Ожидался объект датасета.'); return; }
    for (const k of Object.keys(d)) if (!['key','name','description','definition','seed','engineOverrides'].includes(k)) problem(`${path}.${k}`,'Неизвестное поле датасета.');
    if (!/^[a-zA-Z0-9_.-]{1,80}$/.test(d.key || '') || keys.has(d.key)) problem(`${path}.key`,'Нужен уникальный key датасета.');
    keys.add(d.key);
    if (!String(d.name || '').trim() || !Array.isArray(d.definition?.tables) || !isPlainObject(d.seed)) problem(path,'Нужны name, definition.tables и seed датасета.');
  });
  graph.tasks.forEach((task,i) => {
    const path = `$.tasks[${i}].sql`, s = task?.sql;
    if (task?.type === 'sql-test' && !task.id && graph.scopes.includes('content') && !s) problem(path,'Новому SQL-заданию нужны sql и ссылка на датасет.');
    if (!s) return;
    if (!isPlainObject(s)) { problem(path,'Ожидался объект sql.'); return; }
    if (task.type && task.type !== 'sql-test') problem(path,'Поле sql допустимо только для sql-test.');
    const privateFields = ['referenceSql','comparison','stateCheck','schemaCheck'];
    const allowed = ['dataset','mode','allowMultipleStatements','targets','starterSql','limits',...privateFields];
    for (const k of Object.keys(s)) if (!allowed.includes(k)) problem(`${path}.${k}`,'Неизвестное поле sql.');
    if (!keys.has(s.dataset)) problem(`${path}.dataset`,'Датасет не объявлен в datasets[].');
    if (!['result','state','schema'].includes(s.mode)) problem(`${path}.mode`,'Допустимы result, state или schema.');
    if (!Array.isArray(s.targets) || !s.targets.length) { problem(`${path}.targets`,'Нужен хотя бы один SQL-движок.'); return; }
    if (!graph.scopes.includes('checks') && privateFields.some(k => s[k] != null)) problem(path,'Эталон и проверки требуют scope checks.');
    s.targets.forEach((t,ti) => {
      const p = t?.profile;
      if (!isPlainObject(p) || !['postgresql','mysql','sqlite'].includes(p.engine) || !/^sha256:[0-9a-f]{64}$/.test(p.runtimeDigest || '') || !p.engineVersion || !p.adapterVersion || !isPlainObject(p.settings)) problem(`${path}.targets[${ti}].profile`,'Нужен точный профиль SQL-движка.');
      if (!graph.scopes.includes('checks') && ['referenceSqlOverride','comparisonOverride','stateCheckOverride','schemaCheckOverride'].some(k => t?.[k] != null)) problem(`${path}.targets[${ti}]`,'Проверки SQL-движка требуют scope checks.');
    });
  });
}

export function taskGraphExportIssue(options = {}) {
  return options.includeIds === false && options.includeContent === false
    ? 'Для обратного импорта нужны ID заданий или «Условия и настройки». Включите хотя бы один из этих разделов.' : '';
}

export function taskGraphJsonParseMessage(error, text = '') {
  const message = String(error?.message || '');
  const match = message.match(/position\s+(\d+)/i);
  if (match) {
    const offset = Math.max(0, Math.min(text.length, Number(match[1])));
    const lines = text.slice(0, offset).split('\n');
    return `JSON не читается: строка ${lines.length}, столбец ${lines[lines.length - 1].length + 1}. Проверьте кавычки, запятые и скобки.`;
  }
  return 'JSON не читается. Проверьте двойные кавычки, запятые и парные скобки; комментарии и запятая после последнего элемента недопустимы.';
}
