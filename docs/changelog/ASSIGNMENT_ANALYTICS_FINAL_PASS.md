# Assignment analytics final pass

Final hardening pass for the assignment analytics/proctoring update.

## Fixed in this pass

- Removed accidental session reset on every code/language/layout change in `AssignmentSolvePage`.
  The solve session now opens once per assignment load and keeps using refs for the latest code/language.
- Improved close/unload duration accounting: active hidden/blur periods are finalized before the final event is flushed.
- Improved copy/cut samples by falling back to selected text when the browser does not expose clipboard text for copy/cut events.
- Hardened analytics settings loading on backend: stored partial JSON such as `{ "mode": "proctoring" }` is now expanded through presets and clamped through the same normalization path used by saves.
- Made code accepted counters in `solutions-api` case-insensitive for internal assignment attempts summaries.

## Migration policy

No EF migration files were generated or edited by hand in this pass. Generate them manually with `dotnet ef migrations add ...` after applying the archive.
