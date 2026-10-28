# TaskForge keyboard shortcuts

## Interaction contract

Hold **left Alt** to temporarily show key combinations on registered controls instead of
visible labels. Release Alt to restore labels. This is hold-to-preview, not a sticky
keyboard mode. The Settings / Keyboard shortcuts section also provides a persistent
"show combinations" toggle and a reference dialog. Escape, focus loss, page hiding,
or navigation clears the persistent display. Settings are saved immediately in this
browser under `taskforge.shortcuts.v1`; there is no database change.

The original text, SVGs, DOM order and accessible names remain present. Original content is wrapped in `display: contents` and visually hidden only while the overlay is visible; `aria-label` preserves the button name for assistive technology. An absolutely
positioned, transparent, `aria-hidden` overlay paints the hint without participating in flex/grid
layout. There is no width/height measurement followed by resizing, no extra badge beside
the text, and no root font/padding change. Compact icon controls use a smaller hint font;
icon-only controls show only the key to press after Alt (e.g. "I" for Alt+I) to avoid truncation; Shift is written literally elsewhere.

Alt+Enter clicks the current enabled primary action: submitting code/SQL/image/test/math
answers, starting an attempt, running compiler code, or applying an explicitly prepared
JSON operation. It does not implement a separate API submission path. Existing limits,
disabled conditions, confirmations and progress states remain authoritative.

Navigation:
- Alt+C: courses; Alt+R: own solutions; Alt+L: leaderboard.
- Adding Ctrl to an existing **navigation** chord opens the internal destination in a
  new browser tab without changing the current route.
- Ctrl+Alt+Enter opens the **focused internal link** (focus it with Tab). It is not an
  alternative submit command and never sends a solution. There is no hovered-link fallback.
- Adding Ctrl to save/submit/delete/merge actions does not execute those actions.

Tab-based page sections use Alt+1..9. Those controls must be present and visible.
Administrative navigation uses Alt+Shift+letter and remains role-filtered. Context actions
are not global: e.g. Alt+I/Alt+O only operate where JSON import/export buttons are present.
The reference catalog describes supported contexts, not a guarantee that every command is
available on every page.

## Event safety

`shortcutController.js` owns one window capture listener pair. It resolves the current
DOM at dispatch time, so lazy mounting and enabled-state changes do not retain stale
callbacks. Existing onClick handlers perform the actual work.

- Physical `KeyboardEvent.code` keeps letter positions stable across Russian/English layouts.
- Right Alt, AltGraph, composition/IME, meta-key chords, plain editing keys and unregistered
  browser/window-management chords are not treated as application commands.
- Key repetition and a short dispatch cooldown prevent a held key from producing repeated
  submissions; native `disabled`, disabled fieldsets and `aria-busy` are respected as well.
- The foremost visible modal is the only active scope. Background controls and fallback
  navigation are blocked. Enter in a confirmation modal with no registered primary action
  is consumed rather than implicitly submitting its form.
- Repeated row actions require a focused `data-hotkey-context` or an unambiguous target.
  No arbitrary first-account action is chosen.
- Merge has a shortcut only for **opening** its existing confirmation dialog. Destructive
  deletion, final account merging, logout and template reset have no added shortcuts.
- New tabs are restricted to same-origin HTTP(S) destinations and opened synchronously
  with `noopener,noreferrer`.
- Operating-system and browser-reserved shortcuts may still take precedence. Do not
  override Alt+F4, Alt+arrows or Alt+backquote (Linux window switching). Users may disable this system in Settings.

## Extending the catalog

1. Add a definition to `features/shortcuts/shortcutCatalog.js`. Reuse a context-specific
   chord only when the actions cannot be simultaneously ambiguous.
2. Use the shared `Button` with `shortcut="id"`, or `ShortcutButton`, `ShortcutLink`,
   `ShortcutAnchor`. Do not create a new document/window listener for a page.
3. Existing internal links are recognized only by exact registered destinations; use an
   explicit `shortcut` for dynamic assignment URLs. `shortcutHref` enables safe new-tab
   navigation on a button whose click already navigates there.
4. Preserve native disabled/busy guards. Use `shortcutPriority` only for intentional
   duplicate toolbars; wrap repeatable row controls in `data-hotkey-context`.
5. Give modal panels `role="dialog"`, `aria-modal="true"` and an accessible name.
6. Test with long labels, small icon buttons, both themes, responsive layouts, focused
   editors, open dialogs, and modifier release/focus loss.

## Validation

Repository CI:

```bash
bash scripts/tests/frontend.sh
```

Focused checks after installing the existing frontend dependencies:

```bash
node --test apps/web/scripts/shortcuts-tests/*.test.mjs
CI=true npm --prefix apps/web test -- --watchAll=false --runInBand --testPathPattern=shortcuts
CI=true npm --prefix apps/web run build
```

- The Node catalog tests execute the actual dependency-free source modules.
- `shortcutController.test.js` contains DOM behavior tests for dispatch/safety/lifecycle.
- `ShortcutControls.test.jsx` renders actual React controls, refs, links and field labels.
- Geometry acceptance: record bounding rectangles before Alt, with Alt, with Ctrl+Alt,
  and after release. All rectangles and document scroll dimensions must remain unchanged.
  Measure at least 375, 768, 1024, 1280 and 1720px in default/neo-brutal light/dark themes.

This delivery was checked with 259 Node tests and 38 controller tests in real Chromium.
The controller tests used a minimal test adapter and a synthetic same-origin URL for an
about:blank page; they are not a full React/Monaco/backend integration run. A separate
Chromium geometry fixture generated from the actual pure control JSX, production
controller and CSS passed 20 theme/viewport cases. It supplies surrounding layout
utilities rather than building Tailwind. New React component tests, the complete Jest
suite and production builds still require CI: npm installation in the authoring
container failed with registry DNS errors (EAI_AGAIN). No successful production build
is claimed from the fixture.

## Catalog

| Command ID | Keys | Label | Context |
| --- | --- | --- | --- |
| `nav.news` | `Alt+N` | Лента | navigation |
| `nav.courses` | `Alt+C` | Курсы | navigation |
| `nav.solutions` | `Alt+R` | Мои решения | navigation |
| `nav.leaderboard` | `Alt+L` | Рейтинг | navigation |
| `nav.compiler` | `Alt+K` | Компилятор | navigation |
| `nav.settings` | `Alt+S` | Настройки | navigation |
| `nav.profile` | `Alt+P` | Профиль | navigation |
| `nav.support` | `Alt+H` | Поддержка | navigation |
| `nav.minecraft` | `Alt+M` | Minecraft | navigation |
| `nav.ai` | `Alt+Shift+I` | ИИ: аккаунты | admin |
| `nav.analytics` | `Alt+Shift+A` | Аналитика | admin |
| `nav.activity` | `Alt+Shift+N` | Действия пользователей | admin |
| `nav.users` | `Alt+Shift+U` | Пользователи | admin |
| `nav.groups` | `Alt+Shift+G` | Группы | admin |
| `nav.adminSolutions` | `Alt+Shift+R` | Решения всех участников | admin |
| `nav.badges` | `Alt+Shift+B` | Бейджи | admin |
| `nav.cluster` | `Alt+Shift+L` | Кластер | admin |
| `nav.links` | `Alt+Shift+M` | Связи Minecraft | admin |
| `nav.roles` | `Alt+Shift+F` | Дополнительные роли | admin |
| `nav.assistant` | `Alt+Shift+O` | ИИ-ассистент | admin |
| `help` | `Alt+/` | Справочник сочетаний | navigation |
| `notifications` | `Alt+J` | Уведомления | navigation |
| `editorMode` | `Alt+E` | Режим редактора | editing |
| `primary` | `Alt+Enter` | Отправить решение / запустить код | task |
| `course` | `Alt+B` | К заданиям курса | task |
| `results` | `Alt+Y` | Результаты задания | task |
| `edit` | `Alt+Shift+W` | Редактировать | editing |
| `insights` | `Alt+Shift+Y` | Аналитика задания | editing |
| `save` | `Alt+W` | Сохранить | editing |
| `import` | `Alt+I` | Открыть импорт JSON | editing |
| `export` | `Alt+O` | Открыть экспорт JSON | editing |
| `refresh` | `Alt+U` | Обновить список | page |
| `search` | `Alt+Q` | Поиск на странице | page |
| `filter` | `Alt+A` | Применить фильтры | page |
| `download` | `Alt+V` | Скачать | page |
| `close` | `Alt+X` | Закрыть / остановить | page |
| `fullscreen` | `Alt+Z` | Полный экран | compiler |
| `restart` | `Alt+Shift+Enter` | Перезапустить код | compiler |
| `copy` | `Alt+Shift+V` | Копировать | page |
| `format` | `Alt+Shift+J` | Форматировать JSON | editing |
| `merge` | `Alt+8` | Открыть объединение пары | review |
| `duplicate` | `Alt+9` | Это дубль | review |
| `different` | `Alt+0` | Разные люди | review |
| `ignore` | `Alt+-` | Игнорировать пару | review |
| `tab.1` | `Alt+1` | Раздел 1 текущей страницы | tabs |
| `tab.2` | `Alt+2` | Раздел 2 текущей страницы | tabs |
| `tab.3` | `Alt+3` | Раздел 3 текущей страницы | tabs |
| `tab.4` | `Alt+4` | Раздел 4 текущей страницы | tabs |
| `tab.5` | `Alt+5` | Раздел 5 текущей страницы | tabs |
| `tab.6` | `Alt+6` | Раздел 6 текущей страницы | tabs |
| `tab.7` | `Alt+7` | Раздел 7 текущей страницы | tabs |
| `tab.8` | `Alt+8` | Раздел 8 текущей страницы | tabs |
| `tab.9` | `Alt+9` | Раздел 9 текущей страницы | tabs |

## Linux / UI polish notes (2026-10-29)

- Alt+` and Alt+arrows remain OS/browser-owned; the left-sidebar toggle is still clickable.
- The next-assignment picker still allows selecting a branch; no automatic Alt+period navigation.
- Editor mode uses **Alt+E** (not Alt+Shift+E); Shift is written literally in the hint.
- JSON formatting uses **Alt+Shift+J** instead of Alt+F to avoid browser menu conflicts.
- Hints have **no extra border or opaque background**: buttons keep their existing visual style and geometry.
- The update is announced in `apps/web/public/updates/keyboard-shortcuts.json`.
