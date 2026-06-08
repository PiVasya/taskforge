# Language runner security baseline

The language runners execute untrusted student code, so they should be treated as the highest-risk containers in the stack.
Both dev and prod compose files now apply the same runner security baseline through `x-runner-security` in `deploy/*/compose/30-execution.yaml`.

Current baseline:

- `security_opt: no-new-privileges:true`
- `cap_drop: ALL`
- `pids_limit` through `RUNNER_PIDS_LIMIT`
- memory limit through `RUNNER_MEM_LIMIT`
- writable temporary storage only through `/tmp` tmpfs controlled by `RUNNER_TMPFS_SIZE`

Default limits:

```env
RUNNER_MEM_LIMIT=512m
RUNNER_PIDS_LIMIT=128
RUNNER_TMPFS_SIZE=256m
```

This is not a full sandbox by itself. It is a container-level hardening baseline that reduces damage from buggy or malicious submissions. Keep the language-level timeouts, process killing, file cleanup, and code analyzers enabled as separate layers.

Do not remove these limits to fix a failing task. First check whether the runner writes outside `/tmp`, whether the task genuinely needs more memory, or whether a compiler/runtime package expects a writable cache path that should be moved into tmpfs explicitly.
