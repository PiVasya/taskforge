# Модель безопасности TaskForge OJ

## Цель

Пользовательский исходный код считается полностью недоверенным. Ни один раннер не принимает код только потому, что запрос пришёл из внутренней сети. Перед выполнением требуется краткоживущая криптографическая аттестация отдельного `code-analyzer`, а после неё код повторно проверяется уже в конкретном раннере и запускается под системной песочницей.

Версия политики: `2026-07-28.4`.

## Обязательный путь выполнения

```text
клиент / очередь / image-test
        |
        v
code-analyzer
  - нормализация языка и профиля
  - лексическая и языковая политика
  - безопасное раскрытие C/C++ препроцессора
  - правила конкретного задания
  - подпись точного SHA-256 исходника
        |
        v
worker / execution-api / assignment-api
  - fail-closed при недоступности анализатора
  - передача исходника и подписи без изменений
        |
        v
языковой runner
  - проверка RSA-подписи, срока, языка, профиля и SHA-256
  - AST / semantic / object / bytecode / PE-проверка
  - seccomp, no_new_privs, rlimits, process group, Pdeathsig
  - изолированная внутренняя сеть и read-only контейнер
```

Прямой вызов раннера без действующей аттестации возвращает отказ. Изменение хотя бы одного байта исходника после анализа делает подпись недействительной.

## Code analyzer

`code-analyzer` объединяет ранние проверки из старой compiler-wrapper защиты и новые проверки:

- ограничение исходника до 1 МиБ и HTTP body до 2 МиБ;
- ограничение числа и длины правил задания;
- ограничение одновременных анализов;
- запрет NUL, опасных Unicode-конструкций и кириллицы в исполняемом коде;
- C/C++ allowlist заголовков, запрет локальных и macro-include, token-pasting, trigraphs, inline asm, compiler attributes, unsafe builtins и restricted paths;
- отдельный запуск `clang -E` с очищенным окружением, лимитами CPU/RAM/PID/FD/output и проверкой раскрытого пользовательского кода;
- allowlist модулей Python и запрет динамического выполнения, интроспекции, file/network/process/native escape API;
- запреты для JavaScript, Java, C#, Pascal и image-профилей;
- сохранение правил конкретного задания `forbidden_calls`, `required_calls`, `extra_forbidden`;
- отказ без подписи при любой внутренней ошибке.

Процесс анализатора помечается `non-dumpable`, включает `no_new_privs` и не пишет исходный код в журналы.

## Криптографическая аттестация

Аттестация содержит:

- schema;
- нормализованный язык;
- профиль `standard` или `image`;
- SHA-256 точного UTF-8 исходника;
- версию политики;
- время выдачи и истечения;
- случайный nonce;
- RSA PKCS#1 v1.5 / SHA-256 подпись.

Требования:

- RSA не меньше 3072 бит;
- TTL не больше 5 минут, в compose по умолчанию 120 секунд;
- private key монтируется только в `code-analyzer`;
- раннеры получают только public key;
- private key должен быть обычным файлом, не symlink, размером до 64 КиБ и без group/other permissions;
- `/ready` анализатора и раннеров fail-closed проверяет ключи.

Генерация ключей:

```bash
./scripts/security/generate-code-analyzer-keypair.sh /srv/taskforge/secrets/code-analyzer
```

Для обычного Docker Compose file-backed secret исходный private key должен быть доступен UID `1000` внутри контейнера анализатора и иметь режим `0600`. В compose дополнительно указан target mode `0400`.

## Повторные проверки в раннерах

### C/C++

- компиляция разделена на object и link;
- ELF-проверка undefined/defined symbols;
- запрет startup override, preinit, IFUNC, IRELATIVE и raw syscall instructions;
- linked sandbox guard устанавливается до пользовательского `main`;
- запрет процессов, сети, ptrace, namespaces, mount, BPF, keyring, io_uring, executable memory и других опасных syscall.

### Pascal

- проверка directives и object output;
- linked guard и seccomp;
- отдельная защита PascalABC.NET/FreePascal путей выполнения.

### Java

- компиляция без annotation processors и debug metadata;
- собственный parser `.class` constant pool;
- запрет process, filesystem, network, reflection, method handles, classloaders, native/internal APIs и restricted paths;
- seccomp preload с сохранением только необходимых JVM threads.

### C#

- Roslyn semantic policy;
- запрет unsafe, pointers, stackalloc, extern, P/Invoke, reflection, process/files/network/runtime loader APIs;
- независимая проверка PE metadata, TypeRef, MemberRef, method flags и attributes;
- очищенное окружение, отключённые diagnostics/profilers, heap/output/time limits;
- runner не содержит Redis credentials.

### Python

- независимый AST gate непосредственно перед запуском;
- import allowlist;
- запрет `eval`, `exec`, `compile`, dynamic import, reflection, frame/subclass escapes и private runtime attributes;
- запрет известных file-read обходов через NumPy, PIL, Matplotlib и Tkinter;
- isolated mode `-I -B` и seccomp preload.

### JavaScript

- Node 22;
- syntax check и permission model;
- запрет addons, WebAssembly, string code generation, prototype mutation, workers и shared memory;
- bootstrap удаляет process/global/Buffer/network-capable globals до импорта пользовательского модуля;
- пользовательский код не получает module imports;
- seccomp preload.

### Image runners

- тот же signed analyzer profile `image`;
- safe file open с `O_NOFOLLOW` и проверкой regular file;
- пользовательский процесс останавливается до чтения/конвертации результата;
- Xvfb без TCP и XTEST;
- отдельные sandbox profiles для user process, X server и helper tools;
- жёсткие лимиты PNG/файлов/времени/вывода.

## Контейнерная изоляция

Каждый раннер находится в своей `internal` Docker-сети. Только worker подключён к runner networks. Раннеры не видят друг друга и не подключены к сетям баз данных или Redis.

Для раннеров включены:

- `cap_drop: ALL`;
- `no-new-privileges`;
- explicit non-root UID;
- read-only root filesystem;
- `tmpfs` с `nosuid,nodev`;
- CPU, RAM, swap, PID, core и nofile limits;
- `init: true`;
- отдельная process group и `Pdeathsig=SIGKILL`;
- уничтожение всей группы при timeout/cancellation.

## CI-инварианты

```bash
./scripts/security/check-oj-security.sh
```

Скрипт проверяет синхронность policy version/schema, наличие verifier во всех раннерах, отсутствие обходных execution paths, изоляцию compose networks/secrets, одинаковость Python policy, Docker COPY security files, C guard compilation, Go tests/vet и smoke tests Python/JavaScript.

Оба Docker workflow блокируют сборку, если инвариант нарушен.

## Граница гарантий

Эта схема максимально усиливает текущую контейнерную архитектуру, но не делает утверждение о математически абсолютной невозможности побега. Для следующего уровня изоляции каждую посылку следует запускать в отдельной gVisor sandbox или microVM с одноразовым rootfs и отдельными PID/user/mount/network namespaces. Текущая архитектура специально построена так, чтобы такой backend можно было добавить без удаления analyzer/attestation/runner checks.
