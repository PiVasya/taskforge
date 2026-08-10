# Language runner security baseline

The language runners execute untrusted student code, so they are the highest-risk containers in the stack.
Both dev and prod compose files apply the same container baseline through `x-runner-security` in `deploy/*/compose/30-execution.yaml`.

## Container boundary

Current baseline:

- `security_opt: no-new-privileges:true`
- `cap_drop: ALL`
- an internal runner network without direct external routing
- `pids_limit` through `RUNNER_PIDS_LIMIT`
- memory limit through `RUNNER_MEM_LIMIT`; the Roslyn-based C# service has separate headroom through `CSHARP_RUNNER_MEM_LIMIT`
- a read-only root filesystem
- writable temporary storage only through `/tmp` tmpfs controlled by `RUNNER_TMPFS_SIZE`

Default limits:

```env
RUNNER_MEM_LIMIT=512m
CSHARP_RUNNER_MEM_LIMIT=768m
RUNNER_PIDS_LIMIT=128
RUNNER_TMPFS_SIZE=256m
```

## Submission process environment

Regular non-image runner compiler and submission processes receive an explicit allowlist instead of inheriting the runner service environment. The child environment contains only runtime values such as `PATH`, locale/time-zone values, language-runtime paths where needed, and temporary `HOME`/`TMPDIR` values.

Do not pass database, Redis, object-storage, signing, API, or service-to-service credentials to a runner. The C# runner is intentionally stateless and has no Redis registration or Redis secret in its compose environment.

## C# defense layers

C# submissions are checked twice around compilation. `RoslynSecurityPolicy` validates the syntax and resolved framework symbols before emit, while `ManagedPeSecurityPolicy` validates the generated managed PE before execution. These layers intentionally check different things: the Roslyn layer owns the user-facing framework API denylist, while the PE layer is a structural backstop for native/imported methods, raw standard handles, and forbidden interop or early-execution attributes. Do not reapply the framework namespace/type denylist to every emitted `TypeRef`/`MemberRef`: Roslyn legitimately synthesizes framework references for normal language features such as async/iterator state machines, records, large array initializers, and debugger metadata. Explicit student references to blocked APIs remain rejected by semantic symbol analysis before emit.

The CI regression project at `tools/csharp-runner-policy-check` compiles safe language features such as top-level statements, anonymous types, async code, iterators, records, and compiler-optimized array initialization. It also verifies that process, filesystem, environment-exit, `System.Type`, `RuntimeHelpers`, raw standard handles, explicit debugger attributes, and P/Invoke access stay rejected. Keep this check in the OJ security invariant jobs.

## C and C++ defense layers

C/C++ submissions use independent, fail-closed checks:

1. The code analyzer normalizes C line continuations and rejects dangerous identifiers as tokens. Token-pasting operators (`##` and `%:%:`) are forbidden, so a macro cannot reconstruct blocked APIs such as `system` after the source scan.
2. The C++ runner compiles to an object file before linking and inspects the real ELF symbols, executable instructions, and sensitive ELF metadata, startup-symbol interposition, and pre-init indirect relocations. Dangerous process, dynamic-loader, syscall, networking, namespace, executable-memory, and similar primitives are rejected even when source-level indirection hid their spelling.
3. The final executable is linked with the TaskForge sandbox guard. A `DT_INIT` function uses non-interposable raw syscalls to install a seccomp filter before user constructors run. The filter denies process creation and execution, networking, tracing, namespaces and mounts, executable-memory creation, and other high-risk kernel interfaces.
4. The executable is linked with immediate symbol binding, RELRO, and a non-executable stack, then made read/execute-only for the runner user.

A security-policy rejection is returned as `policy_error` by the runner and is mapped by the execution worker to `PolicyFailed`. Details of internal rules are logged server-side but are not exposed to the submission.

## Operational rules

This is layered hardening, not a reason to expose runners publicly. Runner HTTP endpoints must remain reachable only by trusted internal services. Keep analyzers, timeouts, process-group termination, temporary-file cleanup, container restrictions, and runtime policy checks enabled together.

Do not remove limits or policy layers to fix a failing task. First check whether the task genuinely requires a blocked feature, whether the runner writes outside `/tmp`, or whether a compiler/runtime cache should be moved into tmpfs explicitly. Any exception that permits process creation, networking, dynamic loading, or raw syscalls requires a separate threat review.
