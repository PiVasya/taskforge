-- LAB bootstrap: run as a privileged YugabyteDB role against the yugabyte database.
-- This file contains ONLY database names. No production data deletion, migrations, or passwords.
SELECT format('CREATE DATABASE %I;', n)
FROM (VALUES
    ('taskforge_identity'),
    ('taskforge_education'),
    ('taskforge_content'),
    ('taskforge_tasks'),
    ('taskforge_solutions'),
    ('taskforge_execution'),
    ('taskforge_ai'),
    ('taskforge_support'),
    ('taskforge_minecraft'),
    ('taskforge_files'),
    ('taskforge_notifications'),
    ('taskforge_observability'),
    ('taskforge_telegram_quiz')
) AS databases(n)
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = n)
ORDER BY n
\gexec
