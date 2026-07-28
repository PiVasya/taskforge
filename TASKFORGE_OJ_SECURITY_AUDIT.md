# TaskForge OJ security audit

Дата проверки: 2026-07-28  
Версия централизованной политики: `2026-07-28.4`  
Schema аттестации: `taskforge-code-policy-attestation-v2`

## Проверенные инварианты

- Все восемь Go runner-ов и C# runner требуют краткоживущую RSA-аттестацию точного исходника.
- Worker, execution API и image assignment API работают fail-closed и не отправляют код в runner до анализа.
- Private key доступен только `code-analyzer`; runner-ы получают только public key.
- Каждый runner подключён только к своей internal-сети, имеет read-only rootfs, `cap_drop: ALL`, `no-new-privileges`, non-root UID, CPU/RAM/swap/PID/core/nofile limits и tmpfs.
- Проверки Python standard/image идентичны; устаревший Python HTTP server удалён.
- C/C++ object inspection проверяет запрещённые imports/definitions, ранний код, relocations и raw syscall instructions.
- Java class parser, C# Roslyn/PE policy, Python AST policy и JavaScript bootstrap/permission model присутствуют в execution path.
- Все C seccomp guard/preload исходники собираются с `-Werror` и hardening linker flags.
- Старый compiler-wrapper из ветки 108 отсутствует.

## Выполненные проверки

```text
./scripts/security/check-oj-security.sh     PASS
go test ./... во всех 8 Go runner-модулях PASS
go vet ./... во всех 8 Go runner-модулях  PASS
go test -race: cpp, image-cpp, python,
  pascal, java                            PASS
Python policy smoke tests                 PASS
JavaScript Node 22 bootstrap smoke        PASS
C guard/preload strict compilation        PASS
Compose YAML parse + security invariants  PASS
Workflow integrity check                  PASS
Git whitespace check                      PASS
```

## Ограничение локального окружения проверки

В локальном окружении отсутствуют Docker, Cargo/Rust и .NET SDK. Поэтому полные Docker-сборки `code-analyzer` и C# runner здесь не выполнялись. В обоих build workflows добавлен обязательный `oj-security-invariants` job; окончательная сборка Rust/.NET и образов выполняется CI и не должна публиковать образы при провале проверки.

## Граница модели

Текущая реализация максимально укрепляет существующую Docker-архитектуру, но не объявляет обычный общий Linux kernel абсолютной математической границей. Для максимальной изоляции production-host должен запускать runner-контейнеры через gVisor/runsc или отдельные microVM. Analyzer, signed attestation и все runner-side проверки при этом сохраняются как независимые слои.
