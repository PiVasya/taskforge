#!/usr/bin/env python3
"""Guard the local-only K3s split: Tasks definitions, Solutions all attempts."""
from pathlib import Path
root = Path(__file__).resolve().parents[2]
def source(p): return (root/p).read_text(encoding='utf-8-sig')
tasks = source('services/tasks/assignment-api/Data/TasksDbContext.cs')
solutions = source('services/solutions/api/Data/SolutionsDbContext.cs')
education = source('services/education/api/Data/EducationDbContext.cs')
worker = source('services/solutions/rating-worker/Worker.cs')
assert 'DbSet<Assignment> Assignments' in tasks
assert 'DbSet<TaskAttempt>' not in tasks and 'DbSet<UnifiedTaskAttempt>' not in tasks
assert 'DbSet<AdminSolutionFeedEntry>' not in tasks
assert 'DbSet<TaskDefinition>' not in education and 'DbSet<SqlCatalogResource>' not in education
assert 'DbSet<TaskAttempt> TaskAttempts' in solutions
assert 'DbSet<SolutionSubmission> Submissions' in solutions
assert 'DbSet<UserImageTaskSolution> ImageSolutions' in solutions
assert 'CaptureInteractiveAttemptEvents();' in solutions
assert 'FROM "TaskAttempts"' in worker and 'UseUnifiedRating' not in worker
assert 'LoadTaskRowsAsync(connection, ids' in worker
assert not (root/'services/tasks/assignment-api/Endpoints/Internal/UnifiedAttemptOwnershipEndpoints.cs').exists()
assert not (root/'services/tasks/assignment-api/Endpoints/Realtime/AdminTaskSolutionEventsEndpoints.cs').exists()
assert not (root/'services/solutions/api/Services/Results/OwnershipAttemptRelay.cs').exists()
assert not (root/'tools/ownership-v2/backfill.py').exists()
files = [* (root/'services/tasks/assignment-api/Endpoints').rglob('*.cs'),
         * (root/'services/tasks/assignment-api/Services').rglob('*.cs')]
for p in files:
    text = p.read_text(encoding='utf-8-sig')
    assert 'db.Attempts' not in text, p
assert "['^/api/admin/task-solution-events$','solutions-api']" in source('tools/k3s/local/generate.mjs')
print('PASS: single Solutions attempt owner for five types, Tasks definitions only, single SSE owner')
