import React, { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import {
  ArrowRight,
  BookOpen,
  CheckCircle2,
  ChevronDown,
  Code2,
  FileJson2,
  GraduationCap,
  Layers3,
  ListChecks,
  LockKeyhole,
  PlayCircle,
  Rocket,
  Sparkles,
  Trophy,
  Users,
  Wand2,
  Zap,
} from "lucide-react";
import Layout from "../components/Layout";
import { useAuth } from "../auth/AuthContext";

const featureTabs = [
  {
    id: "courses",
    icon: GraduationCap,
    title: "Курсы и задания",
    short: "Собирай обучение в понятные маршруты.",
    text:
      "Курс выглядит как единая траектория: теория, практические задачи, тесты и прогресс ученика находятся рядом, без ощущения хаоса.",
    previewTitle: "Основы программирования",
    previewSubtitle: "8 заданий • 3 теста • прогресс сохраняется",
    previewLines: ["Ввод и вывод", "Условия", "Циклы", "Массивы"],
    stat: "7 / 12",
  },
  {
    id: "judge",
    icon: Code2,
    title: "Проверка кода",
    short: "Отправил решение — получил результат.",
    text:
      "Платформа подходит для задач с кодом: ученик пишет решение, система запускает проверки и возвращает понятный статус.",
    previewTitle: "Задача: сумма элементов",
    previewSubtitle: "C# • Python • C++ • Java • Pascal • JS",
    previewLines: ["Компиляция", "Тест 1 принят", "Тест 2 принят", "Вердикт: Accepted"],
    stat: "AC",
  },
  {
    id: "tests",
    icon: ListChecks,
    title: "Тесты и ответы",
    short: "Не только кодовые задачи.",
    text:
      "Можно давать задания с вариантами ответа, текстовым ответом, разными типами проверок и обучающими пояснениями.",
    previewTitle: "Тест: логические операции",
    previewSubtitle: "A/B/C + текстовый ответ для B-части",
    previewLines: ["Вопрос 1: выбран B", "Вопрос 2: ответ: цикл", "Регистр не учитывается", "Результат: 9 / 10"],
    stat: "90%",
  },
  {
    id: "editor",
    icon: Wand2,
    title: "Редактор контента",
    short: "Создание материалов без ручного ада.",
    text:
      "Редактор помогает собирать курсы, конспекты, задания и импортировать наборы задач из JSON, когда нужно быстро наполнить платформу.",
    previewTitle: "Редактор курса",
    previewSubtitle: "Задания • конспекты • JSON-импорт",
    previewLines: ["Добавлен конспект", "Загружено 5 задач", "Проверены тесты", "Готово к публикации"],
    stat: "+5",
  },
];

const audienceCards = [
  {
    icon: BookOpen,
    title: "Ученику",
    text: "Видно, что решено, что осталось и где именно ошибка в решении.",
  },
  {
    icon: Users,
    title: "Преподавателю",
    text: "Курсы, задания, группы, проверка решений и понятная структура обучения.",
  },
  {
    icon: LockKeyhole,
    title: "Администратору",
    text: "Роли, пользователи, аналитика, системный статус и контроль платформы.",
  },
];

const metrics = [
  { value: "Код", label: "практические задания" },
  { value: "Тесты", label: "варианты и текстовые ответы" },
  { value: "JSON", label: "быстрый импорт задач" },
  { value: "Рейтинг", label: "мотивация и прогресс" },
];

const faqItems = [
  {
    q: "Почему не сразу логин?",
    a: "Новый пользователь сначала должен понять, что перед ним: учебная платформа, курсы, задачи, проверка кода и прогресс. После этого вход и регистрация выглядят логично.",
  },
  {
    q: "Нужны ли картинки для красивого первого экрана?",
    a: "Нет. Здесь используются карточки, сетки, градиенты, анимации и псевдо-интерфейс. Никакие изображения, баннеры или внешние ассеты не нужны.",
  },
  {
    q: "Страница будет мешать авторизованным пользователям?",
    a: "Нет. Для авторизованного пользователя кнопки меняются на переход в ленту, курсы и личный прогресс. Защищённые страницы остаются защищёнными.",
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
          <div className="landing-preview-kicker">TaskForge workspace</div>
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
        <div className="landing-code-line"><span>const</span> progress = course.solve();</div>
        <div className="landing-code-line"><span>if</span> (progress.accepted) rating.add(15);</div>
        <div className="landing-code-line muted">// Всё это нарисовано CSS, без картинок</div>
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
              <span>Без баннеров и картинок — интерфейс говорит сам за себя</span>
            </div>

            <h1>
              TaskForge — платформа, где учебные задачи реально проверяются
            </h1>

            <p className="landing-lead">
              Курсы, кодовые задания, тесты, JSON-импорт, прогресс, рейтинг и редактор материалов в одном аккуратном рабочем пространстве.
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
              <span><Zap size={15} /> Автопроверка</span>
              <span><FileJson2 size={15} /> JSON-импорт</span>
              <span><Trophy size={15} /> Рейтинг</span>
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
            <h2>Не просто красивая обложка, а интерактивная витрина продукта</h2>
            <p>
              Нажимай на карточки — справа меняется демонстрация. Так пользователь сразу понимает, что находится внутри платформы.
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
              <div className="landing-panel-label"><Layers3 size={16} /> Активный сценарий</div>
              <h3>{activeFeature.title}</h3>
              <p>{activeFeature.text}</p>
              <FeaturePreview feature={activeFeature} />
            </div>
          </div>
        </section>

        <section className="landing-section landing-audience-section" id="for-whom">
          <div className="landing-section-head compact">
            <div className="landing-section-kicker">Для кого</div>
            <h2>Одна платформа для ученика, преподавателя и администратора</h2>
          </div>

          <div className="landing-audience-grid">
            {audienceCards.map((card) => {
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
            <div className="landing-section-kicker">Сценарий</div>
            <h2>Путь пользователя становится понятным до регистрации</h2>
            <div className="landing-flow-line">
              {[
                "Открывает сайт",
                "Понимает возможности",
                "Создаёт аккаунт",
                "Решает задачи",
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
            <h2>Почему такой формат лучше голого логина</h2>
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
            <div className="landing-section-kicker">Готово к старту</div>
            <h2>Сначала показываем ценность, потом просим войти</h2>
            <p>Так главная страница превращает TaskForge из «формы авторизации» в понятный учебный продукт.</p>
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
          <span>Сделано без изображений: только разметка, CSS и существующие иконки.</span>
        </footer>
      </div>
    </Layout>
  );
}
