import React, { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import {
  ArrowRight,
  BookOpen,
  CheckCircle2,
  ChevronDown,
  Code2,
  GraduationCap,
  ListChecks,
  PlayCircle,
  RefreshCw,
  Rocket,
  Sparkles,
  Target,
  Trophy,
  Zap,
} from "lucide-react";
import { useAuth } from "../auth/AuthContext";

const featureTabs = [
  {
    id: "courses",
    icon: GraduationCap,
    title: "Курсы по шагам",
    short: "Понятный маршрут от темы к практике.",
    text:
      "Курс ведёт по темам последовательно: открыл материал, перешёл к заданию, решил, увидел прогресс и продолжил дальше.",
    exampleTitle: "Что видит ученик",
    exampleLines: [
      "список доступных курсов",
      "прогресс внутри выбранного курса",
      "следующее задание без лишних поисков",
    ],
  },
  {
    id: "judge",
    icon: Code2,
    title: "Проверка решений",
    short: "Отправил код — получил результат.",
    text:
      "После отправки решения платформа показывает статус проверки, результат тестов и понятный вердикт: принято или нужно исправить.",
    exampleTitle: "Сценарий проверки",
    exampleLines: [
      "код отправляется на выполнение",
      "тесты проходят один за другим",
      "после успешной проверки задание отмечается решённым",
    ],
  },
  {
    id: "tests",
    icon: ListChecks,
    title: "Тесты и короткие ответы",
    short: "Не каждая проверка должна быть кодом.",
    text:
      "Для теории и закрепления можно использовать обычные тесты и задания с коротким текстовым ответом — быстро, понятно и без перегруза.",
    exampleTitle: "Когда это полезно",
    exampleLines: [
      "проверить знание терминов",
      "закрепить тему после конспекта",
      "дать короткий ответ без запуска кода",
    ],
  },
  {
    id: "progress",
    icon: Trophy,
    title: "Прогресс и мотивация",
    short: "Видно, что уже сделано и что осталось.",
    text:
      "Решённые задания отмечаются в курсе, общий прогресс остаётся перед глазами, а рейтинг добавляет лёгкую соревновательность.",
    exampleTitle: "После решения",
    exampleLines: [
      "задание становится решённым",
      "счётчик курса обновляется",
      "результат сохраняется в истории решений",
    ],
  },
];

const journeyCards = [
  {
    icon: BookOpen,
    title: "Открыл курс",
    text: "Сразу понятно, с какой темы начать и какие задания уже выполнены.",
  },
  {
    icon: Target,
    title: "Решил задачу",
    text: "Читаешь условие, пишешь решение и отправляешь его на проверку.",
  },
  {
    icon: Zap,
    title: "Получил результат",
    text: "Видишь вердикт, пройденные тесты и обновлённый прогресс.",
  },
];

const metrics = [
  { value: "Курсы", label: "обучение по темам" },
  { value: "Задачи", label: "практика на коде" },
  { value: "Проверка", label: "вердикт после отправки" },
  { value: "Прогресс", label: "видно движение вперёд" },
];

const faqItems = [
  {
    q: "Что такое TaskForge?",
    a: "Это учебная платформа для практики программирования: здесь можно проходить курсы, решать задачи, отправлять решения на проверку и следить за прогрессом.",
  },
  {
    q: "Что я увижу после регистрации?",
    a: "После входа откроются курсы, задания, личный профиль, история решений, рейтинг и остальные учебные разделы платформы.",
  },
  {
    q: "Можно ли отслеживать свой прогресс?",
    a: "Да. В курсах видно количество решённых заданий, а уже выполненные задачи визуально отличаются от тех, которые ещё нужно пройти.",
  },
];

const checkSteps = [
  "Код получен",
  "Запуск проверки",
  "Тест вывода: пройден",
  "Задание решено",
];

function SolutionCheckDemo() {
  const [runKey, setRunKey] = useState(0);

  return (
    <div key={runKey} className="landing-check-demo" aria-label="Пример проверки решения в TaskForge">
      <div className="landing-check-toolbar">
        <div className="landing-preview-topbar" aria-hidden="true">
          <span />
          <span />
          <span />
        </div>
        <div className="landing-check-badge">пример задания</div>
      </div>

      <div className="landing-check-task">
        <div>
          <div className="landing-preview-kicker">TaskForge</div>
          <h3>Hello World на C++</h3>
          <p>Базовая программа выводит приветствие на экран.</p>
        </div>
        <div className="landing-check-result-badge">
          <CheckCircle2 size={18} />
          <span>Решено</span>
        </div>
      </div>

      <div className="landing-check-io" aria-label="Пример входных и выходных данных">
        <div>
          <span>Ввод</span>
          <strong>не требуется</strong>
        </div>
        <div>
          <span>Вывод</span>
          <strong>Hello, World!</strong>
        </div>
      </div>

      <div className="landing-demo-code" aria-label="Пример решения">
        <div className="landing-demo-code-head">
          <Code2 size={16} />
          <span>Решение</span>
        </div>
        <div className="landing-demo-code-line" style={{ "--chars": 19, "--delay": "0.25s" }}>
          #include &lt;iostream&gt;
        </div>
        <div className="landing-demo-code-line" style={{ "--chars": 20, "--delay": "1.05s" }}>
          using namespace std;
        </div>
        <div className="landing-demo-code-line" style={{ "--chars": 10, "--delay": "1.85s" }}>
          int main()
        </div>
        <div className="landing-demo-code-line" style={{ "--chars": 1, "--delay": "2.35s" }}>
          {'{'}
        </div>
        <div className="landing-demo-code-line" style={{ "--chars": 28, "--delay": "2.65s" }}>
          &nbsp;&nbsp;&nbsp;&nbsp;cout &lt;&lt; "Hello, World!";
        </div>
        <div className="landing-demo-code-line" style={{ "--chars": 13, "--delay": "3.75s" }}>
          &nbsp;&nbsp;&nbsp;&nbsp;return 0;
        </div>
        <div className="landing-demo-code-line" style={{ "--chars": 1, "--delay": "4.35s" }}>
          {'}'}
        </div>
      </div>

      <div className="landing-submit-row">
        <button type="button" className="landing-demo-submit" onClick={() => setRunKey((value) => value + 1)}>
          <PlayCircle size={17} />
          <span>Отправить решение</span>
        </button>
        <div className="landing-submit-track" aria-hidden="true">
          <span />
        </div>
      </div>

      <div className="landing-check-steps">
        {checkSteps.map((step, index) => (
          <div key={step} className="landing-check-step" style={{ "--delay": `${5.15 + index * 0.38}s` }}>
            <CheckCircle2 size={16} />
            <span>{step}</span>
          </div>
        ))}
      </div>

      <div className="landing-check-final">
        <CheckCircle2 size={20} />
        <div>
          <strong>Accepted</strong>
          <span>Вывод совпал с ожидаемым, прогресс обновлён.</span>
        </div>
        <button type="button" onClick={() => setRunKey((value) => value + 1)} aria-label="Повторить анимацию проверки">
          <RefreshCw size={16} />
        </button>
      </div>
    </div>
  );
}

function FeaturePanel({ feature }) {
  return (
    <div className="landing-feature-panel" role="tabpanel">
      <div className="landing-panel-label"><Sparkles size={16} /> Польза для ученика</div>
      <h3>{feature.title}</h3>
      <p>{feature.text}</p>

      <div className="landing-feature-example">
        <h4>{feature.exampleTitle}</h4>
        <div className="landing-feature-example-list">
          {feature.exampleLines.map((line) => (
            <div key={line} className="landing-feature-example-row">
              <CheckCircle2 size={16} />
              <span>{line}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

function FaqItem({ item, open, onToggle }) {
  return (
    <div className={`landing-faq-item ${open ? "is-open" : ""}`}>
      <button type="button" onClick={onToggle} aria-expanded={open}>
        <span>{item.q}</span>
        <ChevronDown size={18} />
      </button>
      <div className="landing-faq-body">
        <p>{item.a}</p>
      </div>
    </div>
  );
}

export default function LandingPage() {
  const { access, ready } = useAuth();
  const [activeFeatureId, setActiveFeatureId] = useState(featureTabs[1].id);
  const [openFaq, setOpenFaq] = useState(0);

  const activeFeature = useMemo(
    () => featureTabs.find((item) => item.id === activeFeatureId) || featureTabs[0],
    [activeFeatureId],
  );

  const primaryHref = access ? "/news" : "/register";
  const primaryText = access ? "Перейти в TaskForge" : "Начать обучение";
  const secondaryHref = access ? "/courses" : "/login";
  const secondaryText = access ? "Открыть курсы" : "Войти";

  return (
    <>
      <div className="landing-page">
        <section className="landing-hero" id="top">
          <div className="landing-hero-bg" aria-hidden="true">
            <div className="landing-orb landing-orb-one" />
            <div className="landing-orb landing-orb-two" />
            <div className="landing-grid" />
          </div>

          <div className="landing-hero-content">
            <div className="landing-pill">
              <Sparkles size={16} />
              <span>Код → проверка → результат</span>
            </div>

            <h1>
              Учись программировать на задачах, которые сразу проверяются
            </h1>

            <p className="landing-lead">
              Выбирай курс, решай задания, отправляй решения и сразу понимай результат: прошло, не прошло, что уже сделано и куда двигаться дальше.
            </p>

            <div className="landing-hero-actions">
              <Link to={primaryHref} className="landing-btn landing-btn-primary">
                <PlayCircle size={19} />
                <span>{ready ? primaryText : "Открыть TaskForge"}</span>
                <ArrowRight size={18} />
              </Link>
              <Link to={secondaryHref} className="landing-btn landing-btn-ghost">
                <span>{secondaryText}</span>
              </Link>
            </div>

            <div className="landing-hero-points" aria-label="Ключевые преимущества">
              <span><GraduationCap size={15} /> Курсы</span>
              <span><Code2 size={15} /> Проверка кода</span>
              <span><Trophy size={15} /> Прогресс</span>
            </div>
          </div>

          <div className="landing-hero-preview">
            <SolutionCheckDemo />
          </div>
        </section>

        <section className="landing-metrics" aria-label="Возможности платформы">
          {metrics.map((item) => (
            <div key={item.value} className="landing-metric-card">
              <strong>{item.value}</strong>
              <span>{item.label}</span>
            </div>
          ))}
        </section>

        <section className="landing-section" id="features">
          <div className="landing-section-head">
            <div className="landing-section-kicker">Возможности</div>
            <h2>Всё построено вокруг решения задач</h2>
            <p>
              Курс ведёт по темам, задание даёт практику, проверка сразу показывает результат, а прогресс помогает видеть движение вперёд.
            </p>
          </div>

          <div className="landing-feature-grid">
            <div className="landing-feature-tabs" role="tablist" aria-label="Разделы возможностей">
              {featureTabs.map((feature) => {
                const Icon = feature.icon;
                const active = feature.id === activeFeatureId;
                return (
                  <button
                    key={feature.id}
                    type="button"
                    role="tab"
                    aria-selected={active}
                    className={`landing-feature-tab ${active ? "is-active" : ""}`}
                    onClick={() => setActiveFeatureId(feature.id)}
                  >
                    <span className="landing-feature-icon"><Icon size={21} /></span>
                    <span>
                      <strong>{feature.title}</strong>
                      <small>{feature.short}</small>
                    </span>
                  </button>
                );
              })}
            </div>

            <FeaturePanel feature={activeFeature} />
          </div>
        </section>

        <section className="landing-section landing-audience-section" id="journey">
          <div className="landing-section-head compact">
            <div className="landing-section-kicker">Маршрут</div>
            <h2>Один понятный путь вместо лишнего шума</h2>
          </div>

          <div className="landing-audience-grid">
            {journeyCards.map((card) => {
              const Icon = card.icon;
              return (
                <article key={card.title} className="landing-audience-card">
                  <div className="landing-audience-icon"><Icon size={24} /></div>
                  <h3>{card.title}</h3>
                  <p>{card.text}</p>
                </article>
              );
            })}
          </div>
        </section>

        <section className="landing-flow-section" id="flow">
          <div className="landing-flow-card">
            <div className="landing-section-kicker">Как это работает</div>
            <h2>От курса до принятого решения</h2>
            <div className="landing-flow-line">
              {[
                "Выбрал курс",
                "Открыл задание",
                "Отправил код",
                "Получил вердикт",
              ].map((step, index) => (
                <div key={step} className="landing-flow-step">
                  <span>{index + 1}</span>
                  <strong>{step}</strong>
                </div>
              ))}
            </div>
          </div>
        </section>

        <section className="landing-section" id="faq">
          <div className="landing-section-head compact">
            <div className="landing-section-kicker">Вопросы</div>
            <h2>Ответы на частые вопросы</h2>
          </div>

          <div className="landing-faq-list">
            {faqItems.map((item, index) => (
              <FaqItem
                key={item.q}
                item={item}
                open={openFaq === index}
                onToggle={() => setOpenFaq((cur) => (cur === index ? -1 : index))}
              />
            ))}
          </div>
        </section>

        <section className="landing-final-cta">
          <div>
            <div className="landing-section-kicker">Старт</div>
            <h2>Готов начать обучение?</h2>
            <p>Создай аккаунт, выбери курс и переходи к первому заданию.</p>
          </div>
          <div className="landing-final-actions">
            <Link to={primaryHref} className="landing-btn landing-btn-primary">
              <Rocket size={19} />
              <span>{primaryText}</span>
            </Link>
            <Link to="/privacy" className="landing-btn landing-btn-ghost">
              Политика конфиденциальности
            </Link>
          </div>
        </section>

        <footer className="landing-footer">
          <span>© {new Date().getFullYear()} TaskForge</span>
          <span>Учебная платформа для практики программирования.</span>
        </footer>
      </div>
    </>
  );
}
