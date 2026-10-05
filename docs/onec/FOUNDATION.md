# 1C:Enterprise integration

`onec` is a first-class TaskForge code language: authoring, solving, import/export,
execution routing, Monaco highlighting, analyzer policy and signed runner
attestation are wired end-to-end.

The runtime is now server-bootstrapped rather than privately baked into a special
Docker image. Normal CI builds `onec-runner`; a migration installs the proprietary
platform into persistent volumes from an operator-configured URL/local installer,
creates the initial file infobase and then starts the isolated runner.

The initial `onec-code` profile is intentionally narrow: algorithms,
functions/procedures, strings and collections. Filesystem, network, process,
COM/external-component and dynamic-evaluation capabilities remain denied. Future
`onec-query`, `onec-objects`, `onec-documents`, `onec-posting`, `onec-metadata`,
`onec-http`, `onec-ui` and `onec-project` profiles reuse the same boundary with
explicitly broader capabilities/templates.
