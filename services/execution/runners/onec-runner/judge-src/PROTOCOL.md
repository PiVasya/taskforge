# TaskForge 1C `onec-code` Judge v2

The Judge is source-built per submission by the trusted Go driver. The repository
ships only the trusted external-data-processor XML skeleton. There is no manually
precompiled `TaskForgeJudge.epf` artifact.

## Learner contract

A first-generation `onec-code` solution implements:

```1c
Функция Решение(Вход) Экспорт
    // learner code
КонецФункции
```

`Вход` is the code-test input string. The returned value is converted with
`Строка()` and compared to `expectedOutput` using exact string equality.

## Build/execution flow

The driver copies `judge-src` into a private job directory, writes learner code to
`TaskForgeJudge/Ext/ObjectModule.bsl`, writes trusted assertions to the managed
form module, then runs Designer with `/LoadExternalDataProcessorOrReportFromFiles`
to create `TaskForgeJudge.epf`. Syntax/build diagnostics are returned as
`compile_error`.

The finished EPF is executed once for the whole submission against a private copy
of the file infobase. The trusted form catches exceptions from `Объект.Решение`
and writes bounded result JSON directly to a driver-chosen path. Hidden assertions
are compiled into the trusted form module rather than placed in a learner-readable
request JSON file.

## Result shapes

Run mode returns the normal runner shape (`status`, `exitCode`, `stdout`,
`stderr`, `compileStderr`). Test mode returns `{"results":[...]}` with the normal
TaskForge test fields (`input`, `expectedOutput`, `actualOutput`, `passed`,
`status`, `exitCode`, `stderr`, `compileStderr`, `hidden`).

The outer runner still requires a signed `onec` analyzer attestation before the
trusted driver is started. The initial profile denies dynamic execution,
filesystem/network/process/COM/external-component APIs.
