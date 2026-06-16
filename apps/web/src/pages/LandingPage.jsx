import React, { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import {
  ArrowRight,
  BookOpen,
  CheckCircle2,
  ChevronDown,
  Code2,
  GraduationCap,
  Layers3,
  ListChecks,
  PlayCircle,
  Rocket,
  Sparkles,
  Target,
  Trophy,
  Zap,
} from "lucide-react";
import Layout from "../components/Layout";
import { useAuth } from "../auth/AuthContext";

const featureTabs = [
  {
    id: "courses",
    icon: GraduationCap,
    title: "Курсы по шагам",
    short: "Понятный маршрут от темы к практике.",
    text:
      "Курс помогает двигаться последовательно: сначала тема, затем задания, проверки и сохранённый прогресс. Всегда видно, что уже сделано и куда идти дальше.",
    previewTitle: "Основы программирования",
    previewSubtitle: "Теория • практика • прогресс",
    previewLines: ["Ввод и вывод", "Условия", "Циклы", "Массивы"],
    stat: "7 / 12",
  },
  {
    id: "judge",
    icon: Code2,
    title: "Проверка решений",
    short: "Отправил код — получил результат.",
    text:
      "После отправки решения TaskForge показывает статус проверки, результат тестов и помогает быстрее понять, где возникла ошибка.",
    previewTitle: "Задача: сумма элементов",
    previewSubtitle: "Решение отправлено на проверку",
    previewLines: ["Компиляция выполнена", "Тест 1 принят", "Тест 2 принят", "Вердикт: Accepted"],
    stat: "AC",
  },
  {
    id: "tests",
    icon: ListChecks,
    title: "Тесты и короткие ответы",
    short: "Не каждая задача требует кода.",
    text:
      "Для теории и закрепления можно использовать задания с выбором ответа или коротким текстовым ответом. Это удобно для быстрых проверок понимания темы.",
    previewTitle: "Тест: логические операции",
    previewSubtitle: "Выбор ответа и короткие ответы",
    previewLines: ["Вопрос 1: выбран ответ B", "Вопрос 2: введён короткий ответ", "Ответ принят", "Результат: 9 / 10"],
    stat: "90%",
  },
  {
    id: "progress",
    icon: Trophy,
    title: "Прогресс и мотивация",
    short: "Видно, что решено и что осталось.",
    text:
      "Решённые задания отмечаются в курсе, прогресс собирается в понятную шкалу, а рейтинг добавляет лёгкую соревновательность без перегруза интерфейса.",
    previewTitle: "Личный прогресс",
    previewSubtitle: "Курс продолжается с нужного места",
    previewLines: ["7 заданий решено", "5 заданий осталось", "Последнее решение принято", "Рейтинг обновлён"],
    stat: "+15",
  },
];

const journeyCards = [
  {
    icon: BookOpen,
    title: "Начать без путаницы",
    text: "Сразу видно, где начать: выбрать курс, открыть задание и продолжить с нужного места.",
  },
  {
    icon: Target,
    title: "Решать в своём темпе",
    text: "Курсы и задания разбиты на шаги, поэтому проще возвращаться к обучению после паузы.",
  },
  {
    icon: Zap,
    title: "Сразу видеть результат",
    text: "После отправки решения появляется статус проверки и становится понятно, что делать дальше.",
  },
];

const metrics = [
  { value: "Курсы", label: "структурированное обучение" },
  { value: "Задачи", label: "практика по программированию" },
  { value: "Проверка", label: "понятные статусы решений" },
  { value: "Рейтинг", label: "прогресс и мотивация" },
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

function FeaturePreview({ feature }) {
  return (
    <div className="landing-preview-card" aria-label="Демонстрация возможностей TaskForge">
      <div className="landing-preview-topbar">
        <span />
        <span />
        <span />
      </div>

      <div className="landing-preview-header">
        <div>
          <div className="landing-preview-kicker">TaskForge</div>
          <h3>{feature.previewTitle}</h3>
          <p>{feature.previewSubtitle}</p>
        </div>
        <div className="landing-preview-score">{feature.stat}</div>
      </div>

      <div className="landing-progress-shell">
        <div className={`landing-progress-fill landing-progress-fill--${feature.id}`} />
      </div>

      <div className="landing-preview-list">
        {feature.previewLines.map((line, index) => (
          <div key={line} className="landing-preview-row" style={{ "--delay": `${index * 80}ms` }}>
            <CheckCircle2 size={16} />
            <span>{line}</span>
          </div>
        ))}
      </div>

      <div className="landing-code-window">
        <div className="landing-code-line"><span>course</span>.openNextTask();</div>
        <div className="landing-code-line"><span>solution</span>.submit();</div>
        <div className="landing-code-line muted">status: accepted • progress: updated</div>
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
  const [activeFeatureId, setActiveFeatureId] = useState(featureTabs[0].id);
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
    <Layout fullWidth hideFooter>
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
              <span>Учиться проще, когда сразу виден следующий шаг</span>
            </div>

            <h1>
              TaskForge — платформа для практики программирования
            </h1>

            <p className="landing-lead">
              Выбирай курс, решай задания, отправляй решения на проверку и отслеживай прогресс в одном понятном рабочем пространстве.
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
              <span><Code2 size={15} /> Автопроверка</span>
              <span><Trophy size={15} /> Прогресс</span>
            </div>
          </div>

          <div className="landing-hero-preview">
            <FeaturePreview feature={activeFeature} />
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
            <h2>Всё, что нужно для понятного старта</h2>
            <p>
              Выбери карточку — справа изменится пример экрана. Так сразу видно, как проходит обучение внутри TaskForge.
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

            <div className="landing-feature-panel" role="tabpanel">
              <div className="landing-panel-label"><Layers3 size={16} /> Сценарий обучения</div>
              <h3>{activeFeature.title}</h3>
              <p>{activeFeature.text}</p>
              <FeaturePreview feature={activeFeature} />
            </div>
          </div>
        </section>

        <section className="landing-section landing-audience-section" id="journey">
          <div className="landing-section-head compact">
            <div className="landing-section-kicker">Маршрут</div>
            <h2>От первого задания до уверенного результата</h2>
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
            <h2>Обучение разбито на простые действия</h2>
            <div className="landing-flow-line">
              {[
                "Выбираешь курс",
                "Открываешь задание",
                "Отправляешь решение",
                "Видишь прогресс",
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
    </Layout>
  );
}
