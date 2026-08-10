export const TASK_GRAPH_SCHEMA_VERSION = 3;
export const TASK_GRAPH_FORMAT = 'taskforge-task-graph';
export const TASK_GRAPH_COURSE_REF = '$course';
export const TASK_GRAPH_MAX_TASKS = 200;
export const TASK_GRAPH_MAX_CONNECTIONS = 20_000;

const TASK_FIELDS = new Set([
  'key', 'id', 'type', 'title', 'description', 'language', 'allowedLanguages', 'tags',
  'difficulty', 'rating', 'starterCode', 'testCases', 'testSettings', 'questions', 'blocks',
  'codeForbiddenCalls', 'codeRequiredCalls', 'isVisible', 'imageTestReferenceKey',
  'imageTestSimilarityThreshold',
]);
const TOP_LEVEL_FIELDS = new Set(['schemaVersion', 'format', 'tasks', 'connections']);
const CONNECTION_FIELDS = new Set(['from', 'to', 'access']);
const ACCESS_FIELDS = new Set(['hidden', 'sequential']);
const LAYOUT_FIELDS = new Set([
  'nodes', 'edges', 'viewport', 'position', 'positionabsolute', 'x', 'y', 'coordinates',
  'layout', 'positions', 'mapposition', 'nodeid',
]);
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
  format: TASK_GRAPH_FORMAT,
  tasks: [
    clone(codeTask),
    clone(testTask),
    clone(imageTask),
    clone(mathTask),
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

    { from: 'json-basics', to: 'math-blocks' },
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
};

const SIMPLE_CHAIN_EXAMPLE = {
  schemaVersion: TASK_GRAPH_SCHEMA_VERSION,
  format: TASK_GRAPH_FORMAT,
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
  format: TASK_GRAPH_FORMAT,
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
  format: TASK_GRAPH_FORMAT,
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
      { field: 'schemaVersion', text: `Всегда ${TASK_GRAPH_SCHEMA_VERSION}.` },
      { field: 'format', text: `Всегда "${TASK_GRAPH_FORMAT}".` },
      { field: 'tasks', text: 'Задания открытого курса. Их порядок в массиве не управляет картой.' },
      { field: 'connections', text: 'Направленные связи, которые задают путь, развилки, слияния и эффекты доступа.' },
    ],
  },
  {
    key: 'identity',
    title: 'Создание и обновление',
    items: [
      { field: 'key', text: 'Обязательный уникальный ключ внутри файла. На карте и в базе он не сохраняется как координата.' },
      { field: 'id', text: 'Не указывайте для нового задания. Для обновления сохраните id из экспорта текущего курса.' },
      { field: TASK_GRAPH_COURSE_REF, text: 'Зарезервированный источник, обозначающий открытую ноду курса.' },
      { field: 'без connections', text: 'Задание создаётся или обновляется, но остаётся в списке «Не на карте».' },
    ],
  },
  {
    key: 'common',
    title: 'Общие поля задания',
    items: [
      { field: 'type', text: 'code-test, image-test, test или math.' },
      { field: 'title', text: 'Название до 200 символов.' },
      { field: 'description', text: 'Условие задания. Не помещайте сюда эталонное решение.' },
      { field: 'difficulty', text: '1, 2 или 3.' },
      { field: 'rating', text: 'Неотрицательное количество очков.' },
      { field: 'tags', text: 'Строка тегов.' },
      { field: 'isVisible', text: 'Показывать задание ученикам после выполнения правил карты.' },
    ],
  },
  {
    key: 'connections',
    title: 'Пути, развилки и слияния',
    items: [
      { field: 'from', text: `Источник: ${TASK_GRAPH_COURSE_REF} или key задания.` },
      { field: 'to', text: 'key следующего задания.' },
      { field: 'развилка', text: 'Несколько связей с одинаковым from.' },
      { field: 'слияние', text: 'Несколько связей с одинаковым to.' },
      { field: 'конец пути', text: 'У задания нет исходящих связей.' },
      { field: 'цикл', text: 'Запрещён. Граф должен оставаться направленным и ацикличным.' },
    ],
  },
  {
    key: 'access',
    title: 'Эффекты стрелок',
    items: [
      { field: 'access.hidden', text: 'start — начать полное скрытие; stop — закончить; inherit или отсутствие — продолжить текущее состояние.' },
      { field: 'access.sequential', text: 'start — открывать задания по одному; stop — закончить; inherit или отсутствие — продолжить текущее состояние.' },
      { field: 'комбинация', text: 'hidden и sequential независимы и могут начинаться или заканчиваться на одной связи.' },
    ],
  },
  {
    key: 'code',
    title: 'Code test и Image test',
    items: [
      { field: 'language', text: 'Язык стартового кода.' },
      { field: 'allowedLanguages', text: 'Разрешённые языки решения.' },
      { field: 'starterCode', text: 'Код, который ученик увидит в редакторе.' },
      { field: 'testCases', text: 'input, expectedOutput и isHidden. Для image-test также эталон изображения и threshold.' },
      { field: 'codeRequiredCalls', text: 'Вызовы, которые должны присутствовать в решении.' },
      { field: 'codeForbiddenCalls', text: 'Вызовы, которые запрещены условием задания.' },
      { field: 'imageTestSimilarityThreshold', text: 'Общий порог совпадения изображения от 0 до 100.' },
    ],
  },
  {
    key: 'test',
    title: 'Обычный тест',
    items: [
      { field: 'testSettings', text: 'maxAttempts, passPercent, shuffleQuestions, shuffleAnswers, allowReview, attemptTimeLimitsSeconds.' },
      { field: 'single-choice', text: 'options и один key в correctOptionKeys.' },
      { field: 'multi-choice', text: 'options и несколько key в correctOptionKeys.' },
      { field: 'fill', text: 'acceptedAnswers для короткого ответа.' },
      { field: 'text', text: 'acceptedAnswers, caseSensitive и trim для текстового ответа.' },
    ],
  },
  {
    key: 'math',
    title: 'Math-задание',
    items: [
      { field: 'testSettings', text: 'maxAttempts, passPercent, shuffleBlocks, allowReview, attemptTimeLimitsSeconds.' },
      { field: 'info', text: 'Информационный блок без ответа.' },
      { field: 'single-choice / multi-choice', text: 'options и correctOptionKeys.' },
      { field: 'number', text: 'acceptedAnswers и numericTolerance.' },
      { field: 'expression / set', text: 'acceptedAnswers, caseSensitive и trim.' },
      { field: 'order', text: 'orderItems в правильной последовательности.' },
      { field: 'match', text: 'matchLeftItems, matchRightItems и matchPairs.' },
    ],
  },
  {
    key: 'apply',
    title: 'Что делает импорт',
    items: [
      { field: 'задания', text: 'Создаёт задания без id и обновляет задания с id из текущего курса.' },
      { field: 'связи', text: 'Заменяет связи между перечисленными заданиями и входы из текущего курса. Остальная карта сохраняется.' },
      { field: 'позиции', text: 'Существующие ноды остаются на месте. Новые связанные ноды раскладывает TaskForge.' },
      { field: 'не на карте', text: 'Перечисленное задание без связей снимается с карты и остаётся доступным для ручного перетаскивания.' },
      { field: 'лимиты', text: `До ${TASK_GRAPH_MAX_TASKS} заданий и ${TASK_GRAPH_MAX_CONNECTIONS.toLocaleString('ru-RU')} связей за импорт.` },
    ],
  },
  {
    key: 'layout',
    title: 'Расположение нод',
    items: [
      { field: 'координаты', text: 'Не передаются в JSON. Нейросеть описывает смысл графа, а не пиксели.' },
      { field: 'запрещённые поля', text: 'nodes, edges, viewport, position, x, y, coordinates, layout и любые аналоги расположения.' },
    ],
  },
];

export const TASK_GRAPH_AI_PROMPT = `Создай или измени JSON-граф заданий для импорта в TaskForge.

Верни только один валидный JSON без Markdown, пояснений и текста вокруг него.

Обязательная оболочка:
- schemaVersion: ${TASK_GRAPH_SCHEMA_VERSION}
- format: "${TASK_GRAPH_FORMAT}"
- tasks: массив заданий
- connections: массив направленных связей

Правила документа:
1. У каждого задания должен быть уникальный key длиной до 80 символов. key используется только для ссылок внутри этого JSON.
2. При работе с экспортом сохраняй id и key существующих заданий, которые нужно обновить. Для нового задания id не добавляй.
3. Текущий открытый курс обозначается строкой "${TASK_GRAPH_COURSE_REF}" и может быть только значением from.
4. Порядок прохождения задаётся только connections: from -> to. Не добавляй sort.
5. Несколько connections с одинаковым from создают развилку. Несколько connections с одинаковым to создают слияние.
6. Задание без единой входящей или исходящей связи импортируется, но остаётся вне карты.
7. Не создавай циклы, самоссылки и повторяющиеся связи.
8. Эффекты находятся только в access.hidden и access.sequential. Допустимы start, stop и inherit. Отсутствие поля означает inherit.
9. hidden и sequential независимы: их можно начать или закончить вместе либо на разных стрелках.
10. Допустимые типы заданий: code-test, image-test, test, math.
11. Для code-test и image-test нужен непустой testCases. Для test нужны testSettings и questions. Для math нужны testSettings и blocks.
12. В test используй типы вопросов single-choice, multi-choice, fill и text.
13. В math используй виды блоков info, single-choice, multi-choice, number, expression, set, order и match.
14. Не добавляй analyticsSettings. Не помещай эталонное решение или ответы в description.
15. Никогда не добавляй courseId, exportedAt, nodes, edges, viewport, position, positionAbsolute, x, y, coordinates, layout, positions, mapPosition или nodeId.
16. Нейросеть никогда не выбирает расположение нод. Существующие позиции сохраняет TaskForge, новые рассчитывает TaskForge.
17. Используй только канонические поля из примера. Не больше ${TASK_GRAPH_MAX_TASKS} заданий и ${TASK_GRAPH_MAX_CONNECTIONS} связей.

Мега-пример со всеми типами заданий и возможностями графа:
${JSON.stringify(TASK_GRAPH_MEGA_EXAMPLE, null, 2)}

Задание пользователя:
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

function collectLayoutIssues(value, path, issues) {
  if (Array.isArray(value)) {
    value.forEach((item, index) => collectLayoutIssues(item, `${path}[${index}]`, issues));
    return;
  }
  if (!isPlainObject(value)) return;
  for (const [key, item] of Object.entries(value)) {
    const itemPath = `${path}.${key}`;
    if (LAYOUT_FIELDS.has(String(key).toLowerCase())) {
      issues.push({ path: itemPath, message: 'Расположение нод задаёт TaskForge.' });
    }
    collectLayoutIssues(item, itemPath, issues);
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
      || Number(parsed.schemaVersion) === TASK_GRAPH_SCHEMA_VERSION
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
    if (LAYOUT_FIELDS.has(String(key).toLowerCase())) continue;
    if (!TASK_FIELDS.has(key)) issues.push({ path: `${path}.${key}`, message: 'Неизвестное поле задания.' });
  }
  const title = String(task.title || '').trim();
  const type = String(task.type || '').trim().toLowerCase();
  if (!isPatch && !title) issues.push({ path: `${path}.title`, message: 'title обязателен для нового задания.' });
  if (title.length > 200) issues.push({ path: `${path}.title`, message: 'title не должен быть длиннее 200 символов.' });
  if (task.type !== undefined && !['code-test', 'image-test', 'test', 'math'].includes(type)) {
    issues.push({ path: `${path}.type`, message: 'Допустимы code-test, image-test, test и math.' });
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
      tasks: assignments.map((task, index) => ({ ...task, key: `task-${String(index + 1).padStart(3, '0')}` })),
      connections: [],
    };
  }
  return {
    legacy: false,
    schemaVersion: Number(parsed.schemaVersion),
    format: String(parsed.format || ''),
    tasks: Array.isArray(parsed.tasks) ? parsed.tasks : [],
    connections: Array.isArray(parsed.connections) ? parsed.connections : [],
  };
}

export function validateTaskGraphPayload(parsed) {
  const graph = normalizeTaskGraphPayload(parsed);
  const issues = [];
  collectLayoutIssues(parsed, '$', issues);
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
  for (const key of Object.keys(root || {})) {
    if (LAYOUT_FIELDS.has(String(key).toLowerCase())) continue;
    if (!TOP_LEVEL_FIELDS.has(key)) issues.push({ path: `$.${key}`, message: 'Неизвестное поле верхнего уровня.' });
  }
  if (Number(root?.schemaVersion) !== TASK_GRAPH_SCHEMA_VERSION) issues.push({ path: '$.schemaVersion', message: `Ожидается ${TASK_GRAPH_SCHEMA_VERSION}.` });
  if (root?.format !== TASK_GRAPH_FORMAT) issues.push({ path: '$.format', message: `Ожидается "${TASK_GRAPH_FORMAT}".` });
  if (!Array.isArray(root?.tasks)) issues.push({ path: '$.tasks', message: 'Нужен массив заданий.' });
  if (!Array.isArray(root?.connections)) issues.push({ path: '$.connections', message: 'Нужен массив связей.' });
  if (!graph.tasks.length) issues.push({ path: '$.tasks', message: 'Добавьте хотя бы одно задание.' });
  if (graph.tasks.length > TASK_GRAPH_MAX_TASKS) issues.push({ path: '$.tasks', message: `Не больше ${TASK_GRAPH_MAX_TASKS} заданий.` });
  if (graph.connections.length > TASK_GRAPH_MAX_CONNECTIONS) issues.push({ path: '$.connections', message: `Не больше ${TASK_GRAPH_MAX_CONNECTIONS} связей.` });

  const keySet = new Set();
  const idSet = new Set();
  graph.tasks.forEach((task, index) => {
    const path = `$.tasks[${index}]`;
    const key = String(task?.key || '').trim();
    if (!key) issues.push({ path: `${path}.key`, message: 'Укажите уникальный key.' });
    else {
      if (key === TASK_GRAPH_COURSE_REF) issues.push({ path: `${path}.key`, message: `${TASK_GRAPH_COURSE_REF} зарезервирован.` });
      if (key.length > 80 || !/^[\p{L}\p{N}._-]+$/u.test(key)) issues.push({ path: `${path}.key`, message: 'До 80 букв, цифр и символов . _ -.' });
      if (keySet.has(key)) issues.push({ path: `${path}.key`, message: `key "${key}" используется повторно.` });
      keySet.add(key);
    }
    const id = cleanId(task?.id);
    if (id && !isGuid(id)) issues.push({ path: `${path}.id`, message: 'id должен быть GUID существующего задания.' });
    if (id && idSet.has(id)) issues.push({ path: `${path}.id`, message: 'Один id нельзя использовать дважды.' });
    if (id) idSet.add(id);
    issues.push(...validateTaskPayload(task, index, Boolean(id)));
  });

  const connectionSet = new Set();
  const normalizedConnections = [];
  graph.connections.forEach((connection, index) => {
    const path = `$.connections[${index}]`;
    if (!connection || typeof connection !== 'object' || Array.isArray(connection)) {
      issues.push({ path, message: 'Ожидался объект связи.' });
      return;
    }
    for (const key of Object.keys(connection)) {
      if (LAYOUT_FIELDS.has(String(key).toLowerCase())) continue;
      if (!CONNECTION_FIELDS.has(key)) issues.push({ path: `${path}.${key}`, message: 'Неизвестное поле связи.' });
    }
    const from = String(connection.from || '').trim();
    const to = String(connection.to || '').trim();
    if (!from) issues.push({ path: `${path}.from`, message: `Укажите ${TASK_GRAPH_COURSE_REF} или key задания.` });
    else if (from !== TASK_GRAPH_COURSE_REF && !keySet.has(from)) issues.push({ path: `${path}.from`, message: `Задание "${from}" не объявлено.` });
    if (!to) issues.push({ path: `${path}.to`, message: 'Укажите key следующего задания.' });
    else if (to === TASK_GRAPH_COURSE_REF) issues.push({ path: `${path}.to`, message: `${TASK_GRAPH_COURSE_REF} может быть только источником.` });
    else if (!keySet.has(to)) issues.push({ path: `${path}.to`, message: `Задание "${to}" не объявлено.` });
    if (from && from === to) issues.push({ path, message: 'Задание нельзя соединить с самим собой.' });
    const signature = `${from}\u001f${to}`;
    if (from && to && connectionSet.has(signature)) issues.push({ path, message: 'Такая связь уже объявлена.' });
    if (from && to) connectionSet.add(signature);

    const access = connection.access;
    if (access !== undefined && access !== null) {
      if (!access || typeof access !== 'object' || Array.isArray(access)) issues.push({ path: `${path}.access`, message: 'access должен быть объектом.' });
      else {
        for (const key of Object.keys(access)) {
          if (LAYOUT_FIELDS.has(String(key).toLowerCase())) continue;
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

  if (hasCycle([...keySet], normalizedConnections)) issues.push({ path: '$.connections', message: 'Связи не должны образовывать цикл.' });
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
    return `${graph.tasks.length} заданий${topology}${issues.length ? ` · ошибок ${issues.length}` : ''}`;
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

function connectionSignature(connection, identityByKey) {
  const access = normalizeAccess(connection.access);
  const from = connection.from === TASK_GRAPH_COURSE_REF ? TASK_GRAPH_COURSE_REF : (identityByKey.get(connection.from) || `key:${connection.from}`);
  const to = identityByKey.get(connection.to) || `key:${connection.to}`;
  return `${from}\u001f${to}\u001f${access.hidden}\u001f${access.sequential}`;
}

function taskIdentityByKey(tasks, missingPrefix) {
  return new Map(tasks.map((task) => {
    const key = String(task?.key || '');
    const id = cleanId(task?.id);
    return [key, id ? `id:${id}` : `${missingPrefix}:${key}`];
  }));
}

function taskTitleByKey(tasks) {
  return new Map(tasks.map((task) => [String(task?.key || ''), String(task?.title || task?.key || 'Задание')]));
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

export function buildTaskGraphImportDiff(parsed, currentExport) {
  const incomingValidation = validateTaskGraphPayload(parsed);
  const currentValidation = validateTaskGraphPayload(currentExport);
  const incoming = incomingValidation.graph;
  const current = currentValidation.graph;
  const currentById = new Map(current.tasks.map((task) => [cleanId(task.id), task]).filter(([id]) => id));
  const currentTitles = new Set(current.tasks.map((task) => String(task.title || '').trim().toLowerCase()).filter(Boolean));

  const rows = incoming.tasks.map((task, index) => {
    const id = cleanId(task.id ?? task.assignmentId);
    const existing = id ? currentById.get(id) : null;
    const title = String(task.title || existing?.title || `Импорт #${index + 1}`).trim();
    const duplicateTitle = !existing && title && currentTitles.has(title.toLowerCase());
    const changes = existing
      ? DIFF_FIELDS.map(([key, label]) => {
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
      title,
      type: task.type || existing?.type || 'code-test',
      action,
      duplicateTitle,
      changes,
      issues: rowIssues.map((issue) => issue.message),
    };
  });

  const incomingIdentity = taskIdentityByKey(incoming.tasks, 'new');
  const currentIdentity = taskIdentityByKey(current.tasks, 'current');
  const incomingTitle = taskTitleByKey(incoming.tasks);
  const currentTitle = taskTitleByKey(current.tasks);
  const incomingConnections = new Map(incoming.connections.map((connection, index) => [connectionSignature(connection, incomingIdentity), { connection, index }]));
  const currentConnections = new Map(current.connections.map((connection, index) => [connectionSignature(connection, currentIdentity), { connection, index }]));
  const connectedIncomingKeys = connectedTaskKeys(incoming);
  const importedExistingIdentities = new Set(incoming.tasks
    .map((task) => cleanId(task?.id))
    .filter(Boolean)
    .map((id) => `id:${id}`));
  const detachedExistingIdentities = new Set(incoming.tasks
    .filter((task) => cleanId(task?.id) && !connectedIncomingKeys.has(String(task?.key || '')))
    .map((task) => `id:${cleanId(task.id)}`));

  const affectedCurrentConnections = new Map();
  for (const [signature, row] of currentConnections) {
    const sourceIdentity = row.connection.from === TASK_GRAPH_COURSE_REF
      ? TASK_GRAPH_COURSE_REF
      : currentIdentity.get(row.connection.from);
    const targetIdentity = currentIdentity.get(row.connection.to);
    const sourceImported = importedExistingIdentities.has(sourceIdentity);
    const targetImported = importedExistingIdentities.has(targetIdentity);
    const sourceDetached = detachedExistingIdentities.has(sourceIdentity);
    const targetDetached = detachedExistingIdentities.has(targetIdentity);
    const affected = (sourceIdentity === TASK_GRAPH_COURSE_REF && targetImported)
      || (sourceImported && targetImported)
      || sourceDetached
      || targetDetached;
    if (affected) affectedCurrentConnections.set(signature, row);
  }

  const connectionAddedCount = incomingValidation.legacy
    ? 0
    : [...incomingConnections.keys()].filter((signature) => !currentConnections.has(signature)).length;
  const connectionRemovedCount = incomingValidation.legacy
    ? 0
    : [...affectedCurrentConnections.keys()].filter((signature) => !incomingConnections.has(signature)).length;
  const connectionUnchangedCount = incomingValidation.legacy
    ? currentConnections.size
    : [...incomingConnections.keys()].filter((signature) => currentConnections.has(signature)).length;
  const connected = connectedTaskKeys(incoming);
  const unplacedCount = incomingValidation.legacy
    ? 0
    : incoming.tasks.filter((task) => !connected.has(String(task?.key || ''))).length;
  const connectionRows = incomingValidation.legacy
    ? []
    : incoming.connections.map((connection, index) => connectionRow(
        connection,
        index,
        incomingTitle,
        currentConnections.has(connectionSignature(connection, incomingIdentity)) ? 'unchanged' : 'add',
      ));
  const removedConnectionRows = incomingValidation.legacy
    ? []
    : [...affectedCurrentConnections.entries()]
        .filter(([signature]) => !incomingConnections.has(signature))
        .map(([, row]) => connectionRow(row.connection, row.index, currentTitle, 'remove'));

  return {
    total: rows.length,
    createCount: rows.filter((row) => row.action === 'create').length,
    updateCount: rows.filter((row) => row.action === 'update').length,
    unchangedCount: rows.filter((row) => row.action === 'unchanged').length,
    withoutIdCount: rows.filter((row) => !row.id).length,
    duplicateTitleCount: rows.filter((row) => row.duplicateTitle).length,
    validationErrorCount: incomingValidation.issues.length,
    graphIssues: incomingValidation.issues,
    legacy: incomingValidation.legacy,
    connectionCount: incomingValidation.legacy ? current.connections.length : incoming.connections.length,
    connectionAddedCount,
    connectionRemovedCount,
    connectionUnchangedCount,
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
