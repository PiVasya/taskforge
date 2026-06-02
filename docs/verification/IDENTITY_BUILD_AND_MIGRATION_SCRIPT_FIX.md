# Identity build and migration script fix

Fixed compile errors reported by local `dotnet build` for `services/identity/api`.

## Fixed compile errors

1. `TaskForgeRequestSecurity.cs`
   - Replaced invalid `string.Split(',', ';', ' ', StringSplitOptions...)` usage with `Split(new[] { ',', ';', ' ' }, StringSplitOptions...)`.
   - Applied to all active API service copies of `TaskForgeRequestSecurity.cs`.

2. `services/identity/api/Program.cs`
   - Replaced ambiguous method group calls `rows.Select(ToAdminUserDto)` with explicit lambdas `rows.Select(u => ToAdminUserDto(u))`.
   - Fixed bootstrap admin email split overload: `Split(new[] { ',', ';' }, StringSplitOptions...)`.

## Migration scripts

`./scripts/generate-migrations.sh` now performs a real `dotnet build` for every project before checking pending model changes.

Flow now:

1. `dotnet restore`
2. `dotnet build --no-restore`
3. `dotnet ef migrations has-pending-model-changes --no-build`
4. if pending changes exist: `dotnet ef migrations add --no-build`
5. build again after generated migration

This means if migrations are not generated because there are no model changes, the project is still built and compile errors are caught.

`generate-migrations-force.sh` now also builds before and after forced migration generation.

## Verification

`./scripts/verify-structure.sh` now includes a check that migration scripts build projects before deciding/generating migrations.

Static verification passed in this environment:

```text
verification ok
```

`dotnet build` could not be run here because this sandbox has no .NET SDK installed. The updated scripts are intended to catch compile errors locally/CI before migration decisions are made.
