# AI Foundry Wave 10 — Scorecard, Repair Routing, Mutation Groundwork

## Что добавлено

- item-level scorecard aggregation из review chain;
- batch summary aggregation по scorecards и batch review;
- routed repair plan (`description`, `tests`, `solution`, `policy`, `brief`, `general`);
- draft->brief reroute для тяжёлых similarity/batch-context проблем;
- richer batch review payload с batch items, scorecards и peer drafts;
- mutation groundwork внутри `assignment_test_strength_review`.

## Ключевая идея

Теперь review chain не просто хранит разрозненные findings.
Она собирается в единый scorecard с порогами:

- `90+` → ready
- `80..89` → light-repair
- `65..79` → deep-repair
- `50..64` → regenerate-from-brief
- `<50` → rewrite-brief

## Repair routing

Repair больше не считается слепым общим узлом.
Из findings строится `repairPlan`, который определяет:

- primary route;
- список route'ов;
- publish recommendation;
- когда надо чинить сам draft,
- а когда надо откатиться в `brief_repair`.

## Mutation groundwork

`assignment_test_strength_review` теперь пытается оценить тесты не только по количеству,
но и по proxy mutation анализу reference solution:

- переживают ли мутанты существующий test suite;
- какие mutant families оказались неубиты;
- насколько test suite ловит off-by-one / boundary / strip / equality / first-occurrence ошибки.
