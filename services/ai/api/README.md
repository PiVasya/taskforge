# Account Intelligence (legacy ai-api image)

The former LLM assistant has been removed from this MultiNode source branch.
The remaining service performs deterministic account similarity and suspicious
registration analysis, administers account reviews and manages account lifecycle.

- API: /api/admin/ai/account-manager/*
- Admin page: /admin/ai/account-manager
- Workers: AccountIntelligenceWorker, AccountLifecycleWorker (PostgreSQL advisory locks).
- Data: existing taskforge_ai PostgreSQL DB and unchanged EF migration history.
- The ai-api image and folder name are retained for backward compatibility.

There are no chat, prompt, LLM provider, agent run, or AI realtime endpoints.
Historical agent tables remain represented in the EF model until a separately
reviewed, operator-generated migration removes them.

No LLM assistant or agent runtime is deployed by this K3s branch.
