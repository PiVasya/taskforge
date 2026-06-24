# Assignment analytics build fix

Fixed TaskForge.Tasks.Api build failure caused by the local namespace `TaskForge.Tasks.Api.Services.Math` shadowing `System.Math` in the new assignment analytics code.

Changes:
- Replaced unqualified `Math.Abs`, `Math.Max`, `Math.Min`, `Math.Round`, and `Math.Clamp` usages in analytics-related backend files with `global::System.Math.*`.
- Kept migrations untouched: no migration files were generated or edited.
- Rechecked the frontend analytics/editor files with the TypeScript JSX parser.
