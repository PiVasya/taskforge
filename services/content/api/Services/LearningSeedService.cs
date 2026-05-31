using LearningContentService.Data;
using LearningContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LearningContentService.Services;

public static class LearningSeedService
{
    public static async Task SeedInitialCatalogAsync(LearningDbContext db, CancellationToken ct = default)
    {
        if (await db.Courses.AnyAsync(ct))
        {
            return;
        }

        var russian = new LearningCourse
        {
            Slug = "russian-language",
            Title = "Русский язык",
            ShortTitle = "Русский",
            Summary = "Общий курс по русскому языку.",
            SubjectCode = "russian",
            SortOrder = 10,
            IsPublished = true
        };

        var ct2026 = new LearningCourse
        {
            ParentCourseId = russian.Id,
            Slug = "russian-ct-ce-2026",
            Title = "ЦТ / ЦЭ 2026",
            ShortTitle = "ЦТ / ЦЭ 2026",
            Summary = "Подготовка к централизованному тестированию и экзамену 2026 года.",
            SubjectCode = "russian",
            ExamCode = "ct-ce-2026",
            SortOrder = 10,
            IsPublished = true
        };

        var sections = new List<LearningCourse>();
        for (var i = 1; i <= 10; i++)
        {
            sections.Add(new LearningCourse
            {
                ParentCourseId = ct2026.Id,
                Slug = $"russian-ct-ce-2026-a{i}",
                Title = i == 1 ? "A1. Орфография" : $"A{i}",
                ShortTitle = $"A{i}",
                Summary = i == 1 ? "A1. Орфография: гласная в корне слова." : $"Раздел A{i}.",
                SubjectCode = "russian",
                ExamCode = "ct-ce-2026",
                SectionCode = $"A{i}",
                SortOrder = 100 + i,
                IsPublished = true
            });
        }

        for (var i = 1; i <= 11; i++)
        {
            sections.Add(new LearningCourse
            {
                ParentCourseId = ct2026.Id,
                Slug = $"russian-ct-ce-2026-b{i}",
                Title = $"B{i}",
                ShortTitle = $"B{i}",
                Summary = $"Раздел B{i}.",
                SubjectCode = "russian",
                ExamCode = "ct-ce-2026",
                SectionCode = $"B{i}",
                SortOrder = 200 + i,
                IsPublished = true
            });
        }

        db.Courses.Add(russian);
        db.Courses.Add(ct2026);
        db.Courses.AddRange(sections);

        var a1 = sections.First(x => x.SectionCode == "A1");

        db.Pages.Add(new LearningPage
        {
            CourseId = a1.Id,
            Slug = "legacy-conspect-page",
            Title = "Старая страница-конспект",
            Kind = "legacy",
            SortOrder = 999,
            IsPublished = false,
            BodyMarkdown = "Эта страница оставлена как пример старого формата. Основной формат теперь LearningConspect."
        });

        var a1Conspect = new LearningConspect
        {
            CourseId = a1.Id,
            Slug = "a1-orthography-vowel-root",
            Title = "A1. Орфография: гласная в корне слова",
            Subtitle = "Конспект-тренажёр по формату ЦТ/ЦЭ",
            Lead = "Разбираем проверяемые, непроверяемые и чередующиеся гласные в корне, а затем переходим к мини-заданиям.",
            SubjectCode = "russian",
            ExamCode = "ct-ce-2026",
            SectionCode = "A1",
            Kind = "conspect",
            SortOrder = 10,
            EstimatedMinutes = 18,
            BadgesJson = "[\"A1\",\"орфография\",\"ЦТ/ЦЭ\",\"конспект\"]",
            SearchText = "A1 орфография гласная в корне лаг лож гар гор кас кос раст ращ рос проверяемые непроверяемые словарные слова ЦТ ЦЭ",
            ContentJson = """
{
  "schemaVersion": 1,
  "layout": "tabs",
  "startTabId": "theory",
  "hero": {
    "eyebrow": "Русский язык · ЦТ/ЦЭ",
    "title": "A1. Гласная в корне слова",
    "description": "Большой конспект: теория, алгоритм, чередующиеся корни, словарные слова, слова по годам и переход к заданиям.",
    "stats": [
      { "label": "Формат", "value": "конспект + тренировка" },
      { "label": "Время", "value": "15–20 минут" },
      { "label": "Блок", "value": "A1" }
    ]
  },
  "tabs": [
    {
      "id": "theory",
      "title": "Теория",
      "blocks": [
        {
          "id": "core-rule",
          "type": "rule-card",
          "title": "Три типа гласных в корне",
          "tone": "green",
          "items": [
            "Проверяемые: подбираем однокоренное слово, где гласная под ударением.",
            "Непроверяемые: запоминаем словарное написание.",
            "Чередующиеся: применяем правило корня, а не обычную проверку ударением."
          ]
        },
        {
          "type": "warning",
          "title": "Главная ловушка",
          "text": "Для корней с чередованием нельзя просто искать проверочное слово. Например, лаг/лож выбирается по следующей согласной."
        },
        {
          "type": "examples",
          "title": "Быстрые примеры",
          "items": [
            { "source": "пол__жить", "answer": "положить", "comment": "лаг/лож: перед ж пишется о" },
            { "source": "пол__гаться", "answer": "полагаться", "comment": "лаг/лож: перед г пишется а" },
            { "source": "заг__реть", "answer": "загореть", "comment": "гар/гор: без ударения обычно о" }
          ]
        }
      ]
    },
    {
      "id": "algorithm",
      "title": "Алгоритм",
      "blocks": [
        {
          "type": "steps",
          "title": "Как решать A1",
          "items": [
            "Выдели корень и пойми, есть ли чередование.",
            "Если чередования нет — попробуй подобрать проверочное слово.",
            "Если проверить нельзя — вспоминай словарь/частотные слова ЦТ.",
            "Проверь приставки и суффиксы: иногда ошибка не в корне.",
            "После ответа обязательно объясни себе правило одной фразой."
          ]
        },
        {
          "type": "checklist",
          "title": "Самопроверка перед ответом",
          "items": [
            "Это точно корень, а не приставка?",
            "Корень не относится к чередующимся?",
            "Есть ли ударная проверка?",
            "Не является ли слово словарным?"
          ]
        }
      ]
    },
    {
      "id": "roots",
      "title": "Чередующиеся корни",
      "blocks": [
        {
          "type": "table",
          "title": "Мини-таблица корней",
          "columns": ["Корень", "Правило", "Примеры"],
          "rows": [
            ["лаг/лож", "а перед г, о перед ж", "полагать, положить"],
            ["гар/гор", "под ударением а, без ударения о", "загар, загореть"],
            ["кас/кос", "а перед суффиксом -а-", "касаться, коснуться"],
            ["раст/ращ/рос", "а перед ст/щ, о перед с", "растение, выращенный, вырос"]
          ]
        },
        {
          "type": "note",
          "title": "Важно",
          "text": "Таблица специально хранится как структурированный блок, а не HTML. Её можно переиспользовать в редакторе, экспорте и тренировке."
        }
      ]
    },
    {
      "id": "dictionary",
      "title": "Словарные слова",
      "blocks": [
        {
          "type": "dictionary",
          "title": "Частотный словарь A1",
          "groups": [
            { "title": "О", "words": ["абонемент", "аккомпанемент", "компонент", "компромисс"] },
            { "title": "А", "words": ["авангард", "апелляция", "панорама", "катастрофа"] },
            { "title": "Е/И", "words": ["интеллект", "привилегия", "эксперимент", "экспедиция"] }
          ]
        }
      ]
    },
    {
      "id": "years",
      "title": "Слова по годам",
      "blocks": [
        {
          "type": "year-words",
          "title": "Что попадалось в вариантах",
          "years": [
            { "year": 2024, "words": ["положить", "загореть", "коснуться"] },
            { "year": 2023, "words": ["полагаться", "выращенный", "абонемент"] },
            { "year": 2022, "words": ["растение", "компромисс", "привилегия"] }
          ]
        }
      ]
    },
    {
      "id": "practice",
      "title": "Тренировка",
      "blocks": [
        {
          "type": "practice-intro",
          "title": "Переход к заданиям",
          "text": "После конспекта можно открыть подборку мини-заданий по A1. Кнопка берёт фильтр из конспекта и ведёт на страницу задач.",
          "cta": { "label": "Сделать задания A1", "href": "/tasks?sectionCode=A1&type=vowel-choice" }
        }
      ]
    }
  ]
}
"""
        };

        db.Conspects.Add(a1Conspect);
        db.ConspectTaskLinks.Add(new LearningConspectTaskLink
        {
            ConspectId = a1Conspect.Id,
            TaskType = "task-filter",
            SourceService = "quiz-task-service",
            TaskFilterJson = "{\"subjectCode\":\"russian\",\"examCode\":\"ct-ce-2026\",\"sectionCode\":\"A1\",\"type\":\"vowel-choice\"}",
            Title = "Мини-задания по A1",
            ButtonText = "Сделать задания A1",
            GroupTitle = "После конспекта",
            AnchorBlockId = "practice",
            SortOrder = 10,
            IsRequired = true
        });
        db.ConspectTaskLinks.Add(new LearningConspectTaskLink
        {
            ConspectId = a1Conspect.Id,
            TaskType = "quiz-mini",
            SourceService = "quiz-task-service",
            TaskSlug = "a1-vowel-root-polozhit-veschi",
            Title = "Пол__жить вещи",
            ButtonText = "Открыть пример",
            GroupTitle = "Быстрый старт",
            AnchorBlockId = "core-rule",
            SortOrder = 20
        });

        await db.SaveChangesAsync(ct);
    }
}
