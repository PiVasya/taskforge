# AI TipTap statement fix

This update fixes AI-generated assignment statements that were saved as plain paragraphs even when they contained markdown-like code snippets and numbered steps.

## Fixed

- Backend hidden draft creation now converts AI plain text statements into proper TipTap JSON:
  - numbered steps -> `orderedList`
  - bullet steps -> `bulletList`
  - inline backtick code -> `code` marks
  - fenced code -> `codeBlock`
  - headings -> `heading`
- Existing legacy AI drafts that were already saved as paragraph-only TipTap JSON are normalized on the frontend when viewed or edited.
- The editor no longer treats C++ snippets like `<iostream>` as HTML.
- AI task cards in the assistant page render the description through `StatementViewer`, so generated drafts preview closer to the final assignment view.

## Not changed

- `code-test` assignments still show the code editor on the right. That is expected for programming tasks.
- No new database migration was added.
- The previous manual `20260427060000_AiDraftRobustness` migration was removed from this archive.
