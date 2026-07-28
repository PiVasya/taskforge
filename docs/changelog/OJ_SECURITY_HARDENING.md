# OJ security hardening

The execution subsystem now uses layered checks and runtime restrictions for untrusted submissions.

- C/C++ sources are normalized before policy analysis.
- Compiled objects are inspected before linking.
- C++ submissions run with an early seccomp policy.
- Submission child processes receive a minimal environment.
- Runner policy failures are reported separately from compilation failures.
- Stateless runners no longer receive unrelated service credentials.
- Regression tests cover rejected and ordinary C++ programs.
