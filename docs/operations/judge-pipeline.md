# Judge pipeline policy

The normal code submission path must not mark solutions as accepted inside `solutions-api`.

Required flow:

1. `solutions-api` creates a `SolutionSubmission` with a non-terminal status.
2. `solutions-api` loads hidden/autograder tests from `tasks-api` through `/api/internal/assignments/{assignmentId}/judge-spec`.
3. If tests exist, `solutions-api` enqueues a durable execution job through `/api/internal/execution/jobs`.
4. `execution-worker` claims jobs from `/api/internal/execution/jobs/claim-next`.
5. `execution-worker` calls `code-analyzer` before the compiler/runner.
6. If analyzer allows the source, `execution-worker` calls the language runner and completes the execution job.
7. `execution-worker` publishes the final verdict back to `solutions-api` through `/api/internal/solutions/submissions/{submissionId}/verdict`.
8. Rating is updated only from terminal verdicts.

Client supplied tests are ignored for scoring. They can be used only for trial/smoke flows outside the normal submit path.

## Submission response behavior

`solutions-api` waits briefly for a fast worker result. If the verdict is not ready yet, the response stays pending with `Preparing`, `Queued`, or `Running` status. The web frontend polls `/api/me/solutions/{id}` until a terminal verdict arrives.

Terminal scoring verdicts are:

- `Accepted`
- `Rejected`
- `CompileError`
- `PolicyFailed`

Operational non-scoring verdicts include:

- `NoTestsConfigured`
- `JudgeUnavailable`
- `LanguageNotAllowed`

Regular code tasks support six languages through the same runner contract: C#, C++, Java, JavaScript, Pascal and Python. Each runner must expose both `/run/tests` and `/run-tests` aliases; `execution-worker` uses `/run-tests`.

## Queue safety

`execution-api` claims jobs with an atomic status update. Running jobs older than `ExecutionQueue:RunningTimeoutMinutes` can be requeued while `AttemptCount` is below `ExecutionQueue:MaxAttempts`.
