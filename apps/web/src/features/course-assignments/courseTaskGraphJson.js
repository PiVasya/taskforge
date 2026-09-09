export const TASK_GRAPH_SCHEMA_VERSION = 5;
export const TASK_GRAPH_LEGACY_SCHEMA_VERSION = 3;
export const TASK_GRAPH_PREVIOUS_SCHEMA_VERSION = 4;
export const TASK_GRAPH_FORMAT = 'taskforge-task-graph';
export const TASK_GRAPH_COURSE_REF = '$course';
export const TASK_GRAPH_MAX_TASKS = 5000;
export const TASK_GRAPH_MAX_CONNECTIONS = 20_000;

const TASK_FIELDS = new Set([
  'key', 'id', 'course', 'type', 'title', 'description', 'language', 'allowedLanguages', 'tags',
  'difficulty', 'rating', 'starterCode', 'testCases', 'testSettings', 'questions', 'blocks',
  'codeForbiddenCalls', 'codeRequiredCalls', 'isVisible', 'imageTestReferenceKey',
  'imageTestSimilarityThreshold', 'sql',
]);
const TOP_LEVEL_FIELDS = new Set(['schemaVersion', 'format', 'scopes', 'guide', 'courses', 'tasks', 'connections', 'layout', 'datasets']);
const COURSE_FIELDS = new Set(['key', 'id', 'title']);
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
  allowedLanguages: ['cpp', 'python', 'csharp'],
  starterCode: '#include <iostream>\nusing namespace std;\n\nint main()\n{\n    return 0;\n}\n',
  testCases: [
    { input: '2 4', expectedOutput: '6', isHidden: false },
    { input: '-5 12', expectedOutput: '7', isHidden: false },
    { input: '100 250', expectedOutput: '350', isHidden: true },
  ],
  codeRequiredCalls: ['cin', 'cout'],
  codeForbiddenCalls: ['system', 'exec', 'fork'],
  difficulty: 1,
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
  difficulty: 1,
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
  difficulty: 2,
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
  difficulty: 2,
  rating: 2,
  tags: 'математика, блоки',
  isVisible: true,
};

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function taskWith(base, key, title, patch = {}) {
  return { ...clone(base), key, title, ...patch };
}

function simpleCodeTask(key, title, description = 'Решите задание и выведите ответ.') {
  return {
    key,
    type: 'code-test',
    title,
    description,
    language: 'cpp',
    allowedLanguages: ['cpp', 'python', 'csharp'],
    starterCode: '',
    testCases: [
      { input: '1', expectedOutput: '1', isHidden: false },
      { input: '10', expectedOutput: '10', isHidden: true },
    ],
    codeRequiredCalls: [],
    codeForbiddenCalls: ['system', 'exec', 'fork'],
    difficulty: 1,
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
    difficulty: 1,
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
    difficulty: 1,
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
    { key: 'course-advanced', id: '22222222-2222-4222-8222-222222222222', title: 'Углубление' },
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
    viewport: { x: 80, y: 60, zoom: 0.9 },
    positions: {
      [TASK_GRAPH_COURSE_REF]: { x: 0, y: 0 },
      'input-output': { x: 340, y: 0 },
      'json-basics': { x: 680, y: 0 },
      'draw-pixel': { x: 1020, y: -330 },
      'course-advanced': { x: 1020, y: -110 },
      'math-blocks': { x: 1360, y: -110 },
      'sequential-a': { x: 1020, y: 110 },
      'hidden-a': { x: 1020, y: 330 },
      'combined-a': { x: 1020, y: 550 },
      'merge-finish': { x: 1700, y: 0 },
      'final-task': { x: 2040, y: 0 },
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

export const TASK_GRAPH_EXAMPLES = [
  { key: 'mega', title: 'Мега-пример', type: 'вся схема', payload: TASK_GRAPH_MEGA_EXAMPLE },
  { key: 'chain', title: 'Цепочка', type: 'линейный путь', payload: SIMPLE_CHAIN_EXAMPLE },
  { key: 'branch', title: 'Развилка и слияние', type: 'ветви', payload: BRANCH_EXAMPLE },
  { key: 'progression', title: 'Скрытие и по одному', type: 'доступ', payload: PROGRESSION_EXAMPLE },
];

export const TASK_GRAPH_GUIDE_SECTIONS = [
  {
    key: 'document',
    title: 'Документ',
    items: [
      { field: 'schemaVersion', text: `Актуальная версия — ${TASK_GRAPH_SCHEMA_VERSION}. Импорт версии ${TASK_GRAPH_LEGACY_SCHEMA_VERSION} поддерживается для совместимости.` },
      { field: 'format', text: `Всегда "${TASK_GRAPH_FORMAT}".` },
      { field: 'scopes', text: 'Показывает, какие разделы реально присутствуют в файле: ids, content, checks, visibility, connections, connectionAccess, layout.' },
      { field: 'courses', text: 'Вложенные course-ноды. Существующий id привязывает существующий курс; свободный UUID создаёт новый курс с этим id; без id TaskForge создаёт курс и генерирует UUID.' },
      { field: 'tasks', text: 'Задания текущего курса и его подкурсов. Поле course показывает, в каком курсе лежит задание. Порядок массива не задаёт порядок прохождения.' },
      { field: 'connections', text: 'Направленные связи между заданиями и course-нодами. Они задают цепочки, развилки, слияния и эффекты стрелок.' },
      { field: 'layout', text: 'Отдельный раздел координат и viewport. Он не смешивается с содержимым заданий.' },
    ],
  },
  {
    key: 'scopes',
    title: 'Разделы экспорта',
    items: [
      { field: 'ids', text: 'id заданий. Существующий id обновляет конкретное задание, а свободный UUID может быть сохранён за новым заданием.' },
      { field: 'content', text: 'Тип, название, условие, язык, теги, сложность, рейтинг и стартовый код.' },
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
      { field: 'difficulty / rating / tags', text: 'Сложность, очки и теги.' },
    ],
  },
  {
    key: 'checks',
    title: 'Проверки и ответы',
    items: [
      { field: 'code-test / image-test', text: 'testCases, codeRequiredCalls, codeForbiddenCalls; для image-test также эталон и threshold.' },
      { field: 'test', text: 'testSettings и questions; типы single-choice, multi-choice, fill, text.' },
      { field: 'math', text: 'testSettings и blocks; виды info, single-choice, multi-choice, number, expression, set, order, match.' },
    ],
  },
  {
    key: 'import',
    title: 'Выбор при импорте',
    items: [
      { field: 'Содержимое', text: 'Разрешает менять название, условие, тип, языки, теги, сложность, рейтинг и стартовый код существующих заданий.' },
      { field: 'Проверки', text: 'Разрешает менять тесты, ответы, testSettings/math blocks и ограничения кода.' },
      { field: 'Видимость', text: 'Разрешает менять isVisible.' },
      { field: 'Связи', text: 'Разрешает менять топологию from → to для перечисленных заданий.' },
      { field: 'Эффекты связей', text: 'Разрешает менять hidden/sequential. Можно менять эффекты без перестройки топологии.' },
      { field: 'Позиции', text: 'Разрешает применять layout.positions и viewport. Если выключено, существующая раскладка остаётся.' },
      { field: 'id не равно перезаписать всё', text: 'id только выбирает существующее задание. Реально изменяются только включённые категории.' },
    ],
  },
];

export const TASK_GRAPH_AI_PROMPT = `Ты работаешь с JSON-графом курса TaskForge.

Верни только один валидный JSON без Markdown и текста вокруг него.

Актуальная оболочка:
- schemaVersion: ${TASK_GRAPH_SCHEMA_VERSION}
- format: "${TASK_GRAPH_FORMAT}"
- scopes: какие разделы действительно присутствуют
- courses: вложенные course-ноды: существующие и новые
- tasks: задания текущего курса и подкурсов
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

Мега-пример:
${JSON.stringify(TASK_GRAPH_MEGA_EXAMPLE, null, 2)}

Задача пользователя:
`;

function cleanId(value) {
  const text = String(value || '').trim();
  if (!text || text === ZERO_GUID) return '';
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
  if (task.difficulty !== undefined && ![1, 2, 3].includes(Number(task.difficulty))) {
    issues.push({ path: `${path}.difficulty`, message: 'difficulty должен быть 1, 2 или 3.' });
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
      connections: [],
    };
  }
  const schemaVersion = Number(parsed.schemaVersion);
  const scopes = schemaVersion === TASK_GRAPH_LEGACY_SCHEMA_VERSION
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
    connections: Array.isArray(parsed.connections) ? parsed.connections : [],
    layout: isPlainObject(parsed.layout) ? parsed.layout : (parsed.layout ?? null),
  };
}

export function validateTaskGraphPayload(parsed) {
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
  if (![TASK_GRAPH_LEGACY_SCHEMA_VERSION, TASK_GRAPH_PREVIOUS_SCHEMA_VERSION, TASK_GRAPH_SCHEMA_VERSION].includes(schemaVersion)) issues.push({ path: '$.schemaVersion', message: `Поддерживаются ${TASK_GRAPH_LEGACY_SCHEMA_VERSION} и ${TASK_GRAPH_SCHEMA_VERSION}.` });
  if (root?.format !== TASK_GRAPH_FORMAT) issues.push({ path: '$.format', message: `Ожидается "${TASK_GRAPH_FORMAT}".` });
  if (schemaVersion >= TASK_GRAPH_PREVIOUS_SCHEMA_VERSION) {
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
  if (!Array.isArray(root?.connections)) issues.push({ path: '$.connections', message: 'Нужен массив связей.' });
  if (graph.tasks.length > TASK_GRAPH_MAX_TASKS) issues.push({ path: '$.tasks', message: `Не больше ${TASK_GRAPH_MAX_TASKS} заданий.` });
  if (graph.connections.length > TASK_GRAPH_MAX_CONNECTIONS) issues.push({ path: '$.connections', message: `Не больше ${TASK_GRAPH_MAX_CONNECTIONS} связей.` });

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
      : ` · ${graph.connections.length} связей${unplaced ? ` · вне карты ${unplaced}` : ''}`;
    return `${graph.tasks.length} заданий${graph.courses?.length ? ` · курсов ${graph.courses.length}` : ''}${topology}${issues.length ? ` · ошибок ${issues.length}` : ''}`;
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
  ['allowedLanguages', 'Доступные языки'], ['tags', 'Теги'], ['difficulty', 'Сложность'],
  ['rating', 'Рейтинг'], ['starterCode', 'Стартовый код'], ['testCases', 'Тест-кейсы'],
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

export function buildTaskGraphImportDiff(parsed, currentExport, importOptions = {}) {
  const incomingValidation = validateTaskGraphPayload(parsed);
  const currentValidation = validateTaskGraphPayload(currentExport);
  const incoming = incomingValidation.graph;
  const current = currentValidation.graph;
  const scopes = new Set(incoming.scopes || []);
  const options = {
    updateContent: importOptions.updateContent !== false && scopes.has('content'),
    updateChecks: importOptions.updateChecks !== false && scopes.has('checks'),
    updateVisibility: importOptions.updateVisibility !== false && scopes.has('visibility'),
    updateConnections: importOptions.updateConnections !== false && scopes.has('connections'),
    updateConnectionAccess: importOptions.updateConnectionAccess !== false && scopes.has('connectionAccess'),
    updateLayout: importOptions.updateLayout !== false && scopes.has('layout'),
  };
  const currentById = new Map(current.tasks.map((task) => [cleanId(task.id), task]).filter(([id]) => id));
  const currentTitles = new Set(current.tasks.map((task) => String(task.title || '').trim().toLowerCase()).filter(Boolean));
  const incomingTitle = graphTitleByKey(incoming);
  const currentTitle = graphTitleByKey(current);

  const rows = incoming.tasks.map((task, index) => {
    const id = cleanId(task.id ?? task.assignmentId);
    const existing = id ? currentById.get(id) : null;
    const title = String(task.title || existing?.title || `Импорт #${index + 1}`).trim();
    const duplicateTitle = !existing && title && currentTitles.has(title.toLowerCase());
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
    const rowIssues = incomingValidation.issues.filter((issue) => issue.path === `$.tasks[${index}]` || issue.path.startsWith(`$.tasks[${index}].`));
    return {
      index,
      id,
      key: task.key,
      courseRef: String(task?.course || TASK_GRAPH_COURSE_REF),
      courseLabel: incomingTitle.get(String(task?.course || TASK_GRAPH_COURSE_REF)) || String(task?.course || TASK_GRAPH_COURSE_REF),
      title,
      type: task.type || existing?.type || 'code-test',
      action,
      duplicateTitle,
      changes,
      issues: rowIssues.map((issue) => issue.message),
    };
  });

  const incomingIdentity = graphIdentityByKey(incoming, 'new');
  const currentIdentity = graphIdentityByKey(current, 'current');
  const incomingTopologyConnections = new Map(incoming.connections.map((connection, index) => [
    connectionSignature(connection, incomingIdentity, false),
    { connection, index },
  ]));
  const currentTopologyConnections = new Map(current.connections.map((connection, index) => [
    connectionSignature(connection, currentIdentity, false),
    { connection, index },
  ]));
  const importedBoundIdentities = new Set();
  for (const course of incoming.courses || []) {
    const identity = incomingIdentity.get(String(course?.key || ''));
    if (identity?.startsWith('course:')) importedBoundIdentities.add(identity);
  }
  for (const task of incoming.tasks || []) {
    const identity = incomingIdentity.get(String(task?.key || ''));
    if (identity?.startsWith('task:')) importedBoundIdentities.add(identity);
  }

  const affectedCurrentConnections = new Map();
  for (const [signature, row] of currentTopologyConnections) {
    const sourceIdentity = currentIdentity.get(row.connection.from);
    const targetIdentity = currentIdentity.get(row.connection.to);
    const sourceImported = importedBoundIdentities.has(sourceIdentity);
    const targetImported = importedBoundIdentities.has(targetIdentity);
    const affected = (sourceIdentity === TASK_GRAPH_COURSE_REF && targetImported)
      || sourceImported
      || targetImported;
    if (affected) affectedCurrentConnections.set(signature, row);
  }

  const connectionAddedCount = incomingValidation.legacy || !options.updateConnections
    ? 0
    : [...incomingTopologyConnections.keys()].filter((signature) => !currentTopologyConnections.has(signature)).length;
  const connectionRemovedCount = incomingValidation.legacy || !options.updateConnections
    ? 0
    : [...affectedCurrentConnections.keys()].filter((signature) => !incomingTopologyConnections.has(signature)).length;
  const connectionUnchangedCount = incomingValidation.legacy || !options.updateConnections
    ? currentTopologyConnections.size
    : [...incomingTopologyConnections.keys()].filter((signature) => currentTopologyConnections.has(signature)).length;
  const connectionAccessChangedCount = incomingValidation.legacy || !options.updateConnectionAccess
    ? 0
    : [...incomingTopologyConnections.entries()].filter(([signature, row]) => {
        const currentRow = currentTopologyConnections.get(signature);
        return currentRow && !sameValue(normalizeAccess(currentRow.connection.access), normalizeAccess(row.connection.access));
      }).length;
  const connected = connectedTaskKeys(incoming);
  const unplacedCount = incomingValidation.legacy
    ? 0
    : incoming.tasks.filter((task) => !connected.has(String(task?.key || ''))).length;
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
  const removedConnectionRows = incomingValidation.legacy || !options.updateConnections
    ? []
    : [...affectedCurrentConnections.entries()]
        .filter(([signature]) => !incomingTopologyConnections.has(signature))
        .map(([, row]) => connectionRow(row.connection, row.index, currentTitle, 'remove'));


  const currentCourseIds = new Set((current.courses || []).map((course) => cleanId(course?.id)).filter(Boolean));
  const courseCreateCount = (incoming.courses || []).filter((course) => {
    const id = cleanId(course?.id);
    return !id || !currentCourseIds.has(id);
  }).length;
  const courseExistingCount = Math.max(0, (incoming.courses || []).length - courseCreateCount);

  return {
    total: rows.length,
    courseCount: Array.isArray(incoming.courses) ? incoming.courses.length : 0,
    courseCreateCount,
    courseExistingCount,
    createCount: rows.filter((row) => row.action === 'create').length,
    updateCount: rows.filter((row) => row.action === 'update').length,
    unchangedCount: rows.filter((row) => row.action === 'unchanged').length,
    withoutIdCount: rows.filter((row) => !row.id).length,
    duplicateTitleCount: rows.filter((row) => row.duplicateTitle).length,
    validationErrorCount: incomingValidation.issues.length,
    graphIssues: incomingValidation.issues,
    legacy: incomingValidation.legacy,
    scopes: incoming.scopes || [],
    layoutPositionCount: incoming.layout?.positions && typeof incoming.layout.positions === 'object' ? Object.keys(incoming.layout.positions).length : 0,
    importOptions: options,
    connectionCount: incomingValidation.legacy ? current.connections.length : incoming.connections.length,
    connectionAddedCount,
    connectionRemovedCount,
    connectionUnchangedCount,
    connectionAccessChangedCount,
    connectionRows,
    removedConnectionRows,
    unplacedCount,
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
  if (graph.schemaVersion < 5) {
    if (root.datasets !== undefined || graph.tasks.some(t => t?.sql || t?.type === 'sql-test')) problem('$.schemaVersion','SQL resources require schemaVersion 5.');
    return;
  }
  if (!Array.isArray(root.datasets)) { problem('$.datasets','schemaVersion 5 requires datasets[] (empty is allowed).'); return; }
  if (root.datasets.length > 256) problem('$.datasets','At most 256 shared SQL datasets.');
  const keys = new Set();
  root.datasets.forEach((d,i) => {
    const path = `$.datasets[${i}]`;
    if (!isPlainObject(d)) { problem(path,'Expected a dataset object.'); return; }
    for (const k of Object.keys(d)) if (!['key','name','description','definition','seed','engineOverrides'].includes(k)) problem(`${path}.${k}`,'Unknown dataset field.');
    if (!/^[a-zA-Z0-9_.-]{1,80}$/.test(d.key || '') || keys.has(d.key)) problem(`${path}.key`,'Unique dataset key required.');
    keys.add(d.key);
    if (!String(d.name || '').trim() || !Array.isArray(d.definition?.tables) || !isPlainObject(d.seed)) problem(path,'Dataset needs name, definition.tables and seed.');
  });
  graph.tasks.forEach((task,i) => {
    const path = `$.tasks[${i}].sql`, s = task?.sql;
    if (task?.type === 'sql-test' && !task.id && graph.scopes.includes('content') && !s) problem(path,'New SQL assignment needs a SQL spec.');
    if (!s) return;
    if (!isPlainObject(s)) { problem(path,'Expected a SQL specification.'); return; }
    if (task.type && task.type !== 'sql-test') problem(path,'Only sql-test can contain a SQL specification.');
    const privateFields = ['referenceSql','comparison','stateCheck','schemaCheck'];
    const allowed = ['dataset','mode','allowMultipleStatements','targets','starterSql','limits',...privateFields];
    for (const k of Object.keys(s)) if (!allowed.includes(k)) problem(`${path}.${k}`,'Unknown SQL specification field.');
    if (!keys.has(s.dataset)) problem(`${path}.dataset`,'Dataset reference is not declared in datasets[].');
    if (!['result','state','schema'].includes(s.mode)) problem(`${path}.mode`,'Expected result, state or schema.');
    if (!Array.isArray(s.targets) || !s.targets.length) { problem(`${path}.targets`,'At least one engine target is required.'); return; }
    if (!graph.scopes.includes('checks') && privateFields.some(k => s[k] != null)) problem(path,'Private checks require scope checks.');
    s.targets.forEach((t,ti) => {
      const p = t?.profile;
      if (!isPlainObject(p) || !['postgresql','mysql','sqlite'].includes(p.engine) || !/^sha256:[0-9a-f]{64}$/.test(p.runtimeDigest || '') || !p.engineVersion || !p.adapterVersion || !isPlainObject(p.settings)) problem(`${path}.targets[${ti}].profile`,'Exact engine runtime profile required.');
      if (!graph.scopes.includes('checks') && ['referenceSqlOverride','comparisonOverride','stateCheckOverride','schemaCheckOverride'].some(k => t?.[k] != null)) problem(`${path}.targets[${ti}]`,'Private engine checks require scope checks.');
    });
  });
}
