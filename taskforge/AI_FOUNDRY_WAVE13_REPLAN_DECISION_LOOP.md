TaskForge AI Foundry — Wave 13

Что добавлено:
- assignment_batch_replan и stage batch_replan
- in-place replan без сноса всей истории batch item
- decision log digest на уровне batch
- feedback loop state
- planner signal fingerprints для идемпотентного routing
- anti-pattern flags на уровне batch item
- planner feedback теперь умеет не только brief repair, но и reference pack rebuild, а при системном replan сигнале — ставить batch replan

Что важно:
- миграции не добавлялись
- изменены только entities / dto / services / worker / orchestration
