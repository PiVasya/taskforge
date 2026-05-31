# Education API build fix

Fixed `services/education/api/Program.cs` after frontend compatibility endpoints were added.

The previous version used two overloaded top-level helpers named `ToDto` for `Course` and `Group`.
This caused `Enumerable.Select(...)` type inference errors and accidental calls where a `Group` was passed to the `Course` overload during `dotnet publish`.

Fix:
- renamed `ToDto(Course)` to `ToCourseDto(Course)`;
- renamed `ToDto(Group)` to `ToGroupDto(Group)`;
- updated course endpoints to call `ToCourseDto`;
- updated group endpoints to call `ToGroupDto`.

No EF migrations were generated in this archive.
