# AI chat + instruction strictness update

Что добавлено:

- В AI-чате появился управляемый уровень `instructionStrictness` (0..100).
- Настройка живёт в UI как slider и отправляется вместе с сообщением.
- Значение сохраняется в памяти сессии чата и используется в следующих ходах.

Как работает:

- `0..20` — свободный режим: модель может смелее интерпретировать intent и предлагать свои улучшения.
- `21..69` — сбалансированный режим.
- `70..100` — строгий режим: модель должна держать пользовательскую мысль, не расширять scope и сохранять явные фрагменты/запреты из инструкции.

Что изменено в пайплайне:

- Chat payload теперь несёт `instructionStrictness`.
- Память чата хранит `instructionStrictness`.
- Генерация из текста/файла получает:
  - `instructionStrictness`
  - `userInstructionSnapshot`
  - `teachingScript`
- Prompt builder усиливает literal-following при высокой строгости.
- Для генерации и repair температура теперь зависит от строгости.
- Добавлена generic instruction-fidelity validation:
  - если пропали явные пользовательские фрагменты,
  - если нарушены явные запреты,
  - если сломан порядок шагов при запросе на буквальное следование,
  то draft получает `needs-review` и уходит в repair.

Изменённые файлы:

- `taskforge/Data/Models/DTO/AI/AiDtos.cs`
- `taskforge/Services/AI/AiChatService.cs`
- `taskforge/Services/AI/AiJobService.cs`
- `clientapp/src/pages/admin/AdminAiChatPage.jsx`
- `taskforge-ai-worker-external/payload.py`
- `taskforge-ai-worker-external/prompt_builder.py`
- `taskforge-ai-worker-external/worker.py`
- `taskforge-ai-worker-external/repair.py`
- `taskforge-ai-worker-external/tests/test_instruction_strictness.py`
