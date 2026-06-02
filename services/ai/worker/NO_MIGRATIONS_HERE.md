# No migrations here

`ai-worker` is a worker/adapter. It does not own a database.

AI persistence belongs to `services/ai/api`, which owns `taskforge_ai` and has tracked EF Core migrations.
