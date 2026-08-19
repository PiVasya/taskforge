import React from 'react';
import {
  Bot,
  Boxes,
  BrainCircuit,
  Code2,
  Cpu,
  Database,
  FileImage,
  GitBranch,
  HardDrive,
  Image as ImageIcon,
  Layers3,
  MonitorUp,
  Network,
  ShieldCheck,
  TerminalSquare,
  Workflow,
} from 'lucide-react';

const assignmentTypes = [
  {
    title: 'Code test',
    code: 'code-test',
    text: 'Код запускается на наборе тестов. Проверяются stdout, код завершения, ограничения времени и памяти, а результат сохраняется как решение.',
  },
  {
    title: 'Image test',
    code: 'image-test',
    text: 'Программа рисует изображение в виртуальной графической среде. Полученный PNG сравнивается с эталоном, при необходимости одновременно проверяется текстовый вывод.',
  },
  {
    title: 'Test',
    code: 'test',
    text: 'Вопросы с вариантами ответа, настройками попыток и сохранением состояния прохождения.',
  },
  {
    title: 'Math',
    code: 'math',
    text: 'Математические блоки с собственной структурой задания, проверкой ответа и отдельной моделью попыток.',
  },
];

const executionSteps = [
  {
    title: 'Задание формирует проверку',
    text: 'Assignment API хранит тип задания, тесты, ограничения и дополнительные правила проверки. Для обычного code-test решение превращается в execution job.',
  },
  {
    title: 'Исходник анализируется',
    text: 'Перед исполнением Code Analyzer проверяет точный текст программы и выдаёт короткоживущую подписанную attestation. Без неё runner отказывается запускать код.',
  },
  {
    title: 'Worker выбирает runner',
    text: 'Execution worker забирает ожидающую job и отправляет её в отдельный runner языка: C++, C#, Python, Java, JavaScript или Pascal.',
  },
  {
    title: 'Runner выполняет тесты',
    text: 'Каждый runner повторно проверяет attestation, применяет ограничения среды и выполняет тесты. Контейнеры запускаются с ограничениями CPU, памяти и процессов.',
  },
  {
    title: 'Вердикт возвращается в платформу',
    text: 'Worker публикует результат решения, после чего execution job завершается. Прогресс курса и доступные следующие ноды пересчитываются уже по сохранённому результату.',
  },
];

const imageSteps = [
  {
    icon: ShieldCheck,
    title: '1. Анализ исходника',
    text: 'Графическое решение также проходит Code Analyzer. Image runner принимает только attestation для того же языка, профиля image и того же SHA-256 исходника.',
  },
  {
    icon: MonitorUp,
    title: '2. Виртуальный экран',
    text: 'Для каждого запуска создаётся отдельный Xvfb display. По умолчанию это виртуальный экран 1280×1024×24 без физического монитора; TCP у X-сервера отключён, XTEST запрещён.',
  },
  {
    icon: Boxes,
    title: '3. Оконная среда',
    text: 'Поверх Xvfb поднимается минимальный Openbox. Пользовательская программа получает DISPLAY этой изолированной сессии и может открыть настоящее графическое окно.',
  },
  {
    icon: FileImage,
    title: '4. Получение результата',
    text: 'Если программа создала out.png/out.ppm/out.bmp/out.jpg, runner останавливает недоверенный процесс и читает файл. Иначе ищется видимое окно процесса и снимается PNG через ImageMagick; Python runner умеет также захватить корневой X-экран.',
  },
  {
    icon: BrainCircuit,
    title: '5. Сравнение с эталоном',
    text: 'Полученный PNG отправляется в Image Analyzer. Он вычисляет сходство OpenCLIP и perceptual hash, объединяет метрики и сравнивает результат с порогом конкретного теста.',
  },
  {
    icon: HardDrive,
    title: '6. Сохранение результата',
    text: 'Эталонные изображения и сохранённые результаты отправок хранятся через Files API в MinIO/S3, а метаданные файлов — в собственной базе файлового сервиса.',
  },
];

const systemCards = [
  {
    icon: GitBranch,
    title: 'Курс — это граф',
    text: 'Задания и вложенные курсы являются нодами, связи задают переходы и условия открытия. Редактор работает с полным графом, ученик получает только разрешённую ему проекцию.',
  },
  {
    icon: Workflow,
    title: 'Потоковая карта ученика',
    text: 'Learning map передаётся частями в NDJSON: сначала метаданные, затем сегменты графа. После решения задания frontend может запросить delta по projection token вместо полной перезагрузки карты.',
  },
  {
    icon: Database,
    title: 'Разделённые домены данных',
    text: 'Задания, решения, пользователи, файлы, выполнение кода и другие области вынесены в отдельные сервисы. Сервис отвечает за собственную модель и свою базу, а не за общую таблицу всего приложения.',
  },
  {
    icon: Network,
    title: 'Gateway между браузером и сервисами',
    text: 'Frontend работает через публичные маршруты одного сайта. Gateway маршрутизирует запросы в профильные сервисы; внутренние service-to-service endpoint-ы не являются частью браузерного API.',
  },
  {
    icon: Bot,
    title: 'Browser API для автоматизации',
    text: 'Отдельный сервис на Microsoft Playwright поднимает настоящий Chromium, умеет строить accessibility snapshot, делать PNG-снимки и выполнять ограниченные браузерные сессии для AI и автоматизированных проверок.',
  },
  {
    icon: Layers3,
    title: 'Frontend',
    text: 'Основной интерфейс — React 18 SPA с BrowserRouter, lazy-loaded страницами и Monaco Editor для кода. Состояние, которое должно переживать переходы, хранится в глобальных провайдерах или account-scoped query cache.',
  },
];

const imageProfiles = [
  'C++ · OpenGL / GLUT',
  'C++ · Turtle',
  'Pascal · GraphABC',
  'Python · Turtle',
  'Python · matplotlib',
  'Python · Pillow',
];

function SectionHeading({ eyebrow, title, text }) {
  return (
    <div className="max-w-4xl">
      <div className="text-xs font-bold uppercase tracking-[0.18em] text-[rgb(var(--accent))]">{eyebrow}</div>
      <h2 className="mt-2 text-2xl font-semibold tracking-tight sm:text-3xl">{title}</h2>
      {text ? <p className="mt-3 text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-base">{text}</p> : null}
    </div>
  );
}

function FlowStep({ index, title, text }) {
  return (
    <li className="grid gap-3 sm:grid-cols-[52px_1fr] sm:items-start">
      <div className="grid h-11 w-11 place-items-center rounded-xl border border-[rgb(var(--border))] bg-[rgb(var(--muted))] font-mono text-sm font-bold">
        {String(index + 1).padStart(2, '0')}
      </div>
      <div>
        <h3 className="font-semibold">{title}</h3>
        <p className="mt-1 text-sm leading-7 text-[rgb(var(--text-muted))]">{text}</p>
      </div>
    </li>
  );
}

export default function TechnologyPage() {
  return (
    <div className="mx-auto max-w-7xl space-y-8 pb-12">
      <header className="card overflow-hidden p-6 sm:p-8 lg:p-10">
        <div className="flex items-center gap-3 text-sm font-semibold text-[rgb(var(--accent))]">
          <Cpu size={20} aria-hidden="true" />
          <span>Архитектура платформы</span>
        </div>
        <h1 className="mt-4 max-w-5xl text-3xl font-semibold tracking-tight sm:text-5xl lg:text-6xl">
          Как технически устроен TaskForge
        </h1>
        <p className="mt-5 max-w-4xl text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-base sm:leading-8">
          Курсы здесь собираются в граф, код выполняется в отдельных runner-сервисах, а графические задания получают настоящий кадр из виртуального X-сервера и сравнивают его с эталоном нейронной моделью. Ниже — основные механизмы платформы без привязки к конкретному релизу.
        </p>
        <div className="mt-6 flex flex-wrap gap-2" aria-label="Ключевые технологии">
          {['React 18', 'ASP.NET Core', 'PostgreSQL', 'MinIO / S3', 'Xvfb', 'OpenCLIP', 'Playwright', 'Docker'].map((item) => (
            <span key={item} className="rounded-full border border-[rgb(var(--border))] bg-[rgb(var(--muted))] px-3 py-1.5 text-xs font-semibold">
              {item}
            </span>
          ))}
        </div>
      </header>

      <section className="space-y-5">
        <SectionHeading
          eyebrow="Учебная модель"
          title="Задание — не только поле с кодом"
          text="Assignment API нормализует задания в несколько режимов проверки. Тип определяет, какие данные хранятся у задания, какой интерфейс показывается пользователю и какой конвейер проверки запускается."
        />
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
          {assignmentTypes.map((item) => (
            <article key={item.code} className="card p-5">
              <div className="font-mono text-xs font-bold text-[rgb(var(--accent))]">{item.code}</div>
              <h3 className="mt-2 text-lg font-semibold">{item.title}</h3>
              <p className="mt-3 text-sm leading-7 text-[rgb(var(--text-muted))]">{item.text}</p>
            </article>
          ))}
        </div>
      </section>

      <section className="grid gap-5 lg:grid-cols-[0.95fr_1.05fr]">
        <article className="card p-6 sm:p-7">
          <SectionHeading
            eyebrow="Course graph"
            title="Прогресс строится по графу"
            text="Курс хранит не только сортированный список задач. В граф можно включать задания и дочерние курсы, соединять их направленными связями и задавать доступность переходов."
          />
          <div className="mt-6 space-y-4 text-sm leading-7 text-[rgb(var(--text-muted))]">
            <p>
              Ученик не получает весь редакторский документ. Сервер вычисляет learner-проекцию: видимые сейчас ноды, связи, решённые задания и доступные части вложенных курсов.
            </p>
            <p>
              Большая карта может приходить потоково как <code>application/x-ndjson</code>: событие <code>meta</code>, затем набор <code>segment</code> и финальное <code>done</code>. После изменения прогресса используется delta-запрос с projection token, чтобы не пересобирать интерфейс целиком.
            </p>
          </div>
        </article>

        <article className="card p-6 sm:p-7">
          <div className="flex items-center gap-3">
            <TerminalSquare size={22} className="text-[rgb(var(--accent))]" aria-hidden="true" />
            <h2 className="text-2xl font-semibold tracking-tight">Путь обычного code-test</h2>
          </div>
          <ol className="mt-6 space-y-5">
            {executionSteps.map((step, index) => (
              <FlowStep key={step.title} index={index} {...step} />
            ))}
          </ol>
        </article>
      </section>

      <section className="card p-6 sm:p-8 lg:p-9">
        <SectionHeading
          eyebrow="Image test"
          title="Как TaskForge запускает графику без физического экрана"
          text="Графические задания используют отдельные image runner-ы. Они не подменяют рисунок API-вызовами: программа реально запускается в Linux-графической среде и рисует так, как делала бы это на обычном рабочем столе."
        />

        <div className="mt-6 flex flex-wrap gap-2">
          {imageProfiles.map((profile) => (
            <span key={profile} className="rounded-lg border border-[rgb(var(--border))] px-3 py-2 text-xs font-semibold">
              {profile}
            </span>
          ))}
        </div>

        <div className="mt-7 grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {imageSteps.map(({ icon: Icon, title, text }) => (
            <article key={title} className="rounded-2xl border border-[rgb(var(--border))] bg-[rgb(var(--muted))] p-5">
              <Icon size={22} className="text-[rgb(var(--accent))]" aria-hidden="true" />
              <h3 className="mt-4 font-semibold">{title}</h3>
              <p className="mt-2 text-sm leading-7 text-[rgb(var(--text-muted))]">{text}</p>
            </article>
          ))}
        </div>

        <div className="mt-7 grid gap-4 lg:grid-cols-2">
          <div className="rounded-2xl border border-[rgb(var(--border))] p-5 sm:p-6">
            <div className="flex items-center gap-3">
              <ImageIcon size={21} className="text-[rgb(var(--accent))]" aria-hidden="true" />
              <h3 className="text-lg font-semibold">Как считается похожесть</h3>
            </div>
            <p className="mt-3 text-sm leading-7 text-[rgb(var(--text-muted))]">
              Image Analyzer использует OpenCLIP <strong>ViT-L-14</strong> и perceptual hash. По умолчанию итоговая метрика складывается из 85% CLIP similarity и 15% pHash similarity. Для каждого теста можно задать собственный процент прохождения.
            </p>
          </div>
          <div className="rounded-2xl border border-[rgb(var(--border))] p-5 sm:p-6">
            <div className="flex items-center gap-3">
              <Code2 size={21} className="text-[rgb(var(--accent))]" aria-hidden="true" />
              <h3 className="text-lg font-semibold">Что именно может считаться результатом</h3>
            </div>
            <p className="mt-3 text-sm leading-7 text-[rgb(var(--text-muted))]">
              Runner сначала принимает явный графический файл, если программа его создала. Для оконных библиотек он получает кадр непосредственно из виртуального display. Поэтому один механизм покрывает GLUT, Turtle, GraphABC, matplotlib, Pillow и другие совместимые способы рисования.
            </p>
          </div>
        </div>
      </section>

      <section className="space-y-5">
        <SectionHeading
          eyebrow="Сервисы"
          title="Из каких технических блоков складывается платформа"
          text="Основные механизмы разделены по назначению: UI не исполняет код, файловый сервис не проверяет решения, а runner-ы не владеют учебными данными."
        />
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {systemCards.map(({ icon: Icon, title, text }) => (
            <article key={title} className="card p-5 sm:p-6">
              <Icon size={22} className="text-[rgb(var(--accent))]" aria-hidden="true" />
              <h3 className="mt-4 text-lg font-semibold">{title}</h3>
              <p className="mt-2 text-sm leading-7 text-[rgb(var(--text-muted))]">{text}</p>
            </article>
          ))}
        </div>
      </section>

      <section className="grid gap-4 md:grid-cols-3">
        <article className="card p-5 sm:p-6">
          <ShieldCheck size={22} className="text-[rgb(var(--accent))]" aria-hidden="true" />
          <h2 className="mt-4 text-lg font-semibold">Изоляция runner-ов</h2>
          <p className="mt-2 text-sm leading-7 text-[rgb(var(--text-muted))]">
            Production-контейнеры runner-ов работают без дополнительных Linux capabilities, с <code>no-new-privileges</code>, read-only root filesystem и лимитами CPU, памяти и количества процессов.
          </p>
        </article>
        <article className="card p-5 sm:p-6">
          <HardDrive size={22} className="text-[rgb(var(--accent))]" aria-hidden="true" />
          <h2 className="mt-4 text-lg font-semibold">Файлы отдельно от БД</h2>
          <p className="mt-2 text-sm leading-7 text-[rgb(var(--text-muted))]">
            Бинарные объекты хранятся в S3-совместимом MinIO. Files API отвечает за загрузку и выдачу объектов, а PostgreSQL хранит их метаданные и связь с платформой.
          </p>
        </article>
        <article className="card p-5 sm:p-6">
          <BrainCircuit size={22} className="text-[rgb(var(--accent))]" aria-hidden="true" />
          <h2 className="mt-4 text-lg font-semibold">ML вынесен отдельно</h2>
          <p className="mt-2 text-sm leading-7 text-[rgb(var(--text-muted))]">
            Image Analyzer изолирован в собственном контейнере вместе с PyTorch/OpenCLIP. Основной API не тянет тяжёлые ML-зависимости и общается с анализатором по внутреннему HTTP.
          </p>
        </article>
      </section>
    </div>
  );
}
