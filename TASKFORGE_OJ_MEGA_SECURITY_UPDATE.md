# TaskForge OJ mega security update

В этом архиве объединены и усилены проверки из веток 108 и 144.

Главное изменение: `code-analyzer` теперь не является рекомендательной проверкой. Он выдаёт краткоживущую RSA-подпись точного исходника, а каждый runner отказывается запускать код без корректной подписи. Worker, execution API и image assignment API работают fail-closed.

Дополнительно добавлены языковые проверки после компиляции/парсинга, расширены seccomp-профили, сохранена изоляция контейнеров, сетей, процессов, секретов, файлов и вывода. Подробная модель: `docs/security/OJ_MEGA_SECURITY_MODEL.md`.

Проверка инвариантов:

```bash
./scripts/security/check-oj-security.sh
```

Генерация ключей:

```bash
./scripts/security/generate-code-analyzer-keypair.sh /srv/taskforge/secrets/code-analyzer
```
