unit TaskForgeSandboxGuard;

{$mode objfpc}{$H+}
{$L /app/taskforge_pascal_sandbox_guard.o}

interface

uses
  cthreads;

implementation

procedure TaskForgePascalSandboxInit; cdecl; external name 'taskforge_pascal_sandbox_init';

initialization
  TaskForgePascalSandboxInit;

end.
