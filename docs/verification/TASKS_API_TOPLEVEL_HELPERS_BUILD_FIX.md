# Tasks API top-level helper build fix

Fixed `services/tasks/assignment-api/Program.cs` after the monolith parity transfer.

What was wrong:

- `JsonOpts` was emitted as a top-level `static readonly` field. Top-level programs can contain statements/local functions, but not fields in the statement area.
- `TaskSpec`, `MathSpec`, `TestQuestion`, and `MathBlock` records had methods that depended on local helper functions declared in the generated top-level program. Additional top-level types cannot reliably call those local helpers.
- Some helper functions were emitted after top-level record declarations, causing `CS8803`.

What changed:

- Replaced the top-level field with a local `JsonOptions()` factory function.
- Moved task/math JSON parsing, public DTO, and review-node generation into top-level local helper functions before any record declarations.
- Converted task/math records back to plain DTO records without helper methods.

The migration scripts already build each project before checking/generating migrations, so this kind of compile error is now caught before Docker build.
