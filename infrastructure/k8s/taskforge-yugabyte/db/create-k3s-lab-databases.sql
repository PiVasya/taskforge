-- LAB bootstrap: run as a privileged YugabyteDB role against the yugabyte database.
-- This file contains ONLY database names. No production data deletion, migrations, or passwords.
SELECT format('CREATE DATABASE %I;', n)
FROM (VALUES
    ('taskforge_k3s_identity'),
    ('taskforge_k3s_education'),
    ('taskforge_k3s_content'),
    ('taskforge_k3s_tasks'),
    ('taskforge_k3s_solutions'),
    ('taskforge_k3s_execution'),
    ('taskforge_k3s_ai'),
    ('taskforge_k3s_support'),
    ('taskforge_k3s_minecraft'),
    ('taskforge_k3s_files'),
    ('taskforge_k3s_notifications'),
    ('taskforge_k3s_observability'),
    ('taskforge_k3s_telegram_quiz')
) AS databases(n)
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = n)
ORDER BY n
\gexec
