# AI chat-first drafting workflow

## Что изменено

Вместо default-паттерна `chat -> queue_generate_*` теперь основной UX такой:

1. Пользователь обсуждает идею задачи в чате.
2. AI предлагает 1 или несколько примерных условий.
3. Эти примерные условия сохраняются в памяти сессии как `currentDraftBlueprint`.
4. Пользователь правит/одобряет варианты в чате.
5. Только после явного одобрения AI вызывает `finalize_chat_blueprint` и создаёт полноценные draft-черновики.

## Новые chat tools

- `save_chat_blueprint`
- `show_chat_blueprint`
- `drop_chat_blueprint`
- `finalize_chat_blueprint`

## Что осталось

Старые `queue_generate_from_text`, `queue_generate_from_file`, `queue_generate_batch` не удалены физически, но больше не являются default UX для обычного generation-диалога в чате.

## Файлы

- `taskforge/Data/Models/DTO/AI/AiDtos.cs`
- `taskforge/Services/AI/AiChatService.cs`
- `taskforge-ai-worker-external/worker.py`
- `taskforge-ai-worker-external/prompt_builder.py`
- `taskforge-ai-worker-external/tests/test_chat_memory_routing.py`
- `taskforge-ai-worker-external/tests/test_chat_strict_mode.py`
- `clientapp/src/pages/admin/AdminAiChatPage.jsx`
