#!/usr/bin/env python3
"""Fail closed if the admin feed ceases to be durable, admin-only or region-safe."""
from pathlib import Path
import sys
root = Path(__file__).resolve().parents[2]
def body(path):return (root/path).read_text(encoding='utf-8-sig')
try:
    shared = body('services/shared/Realtime/AdminSolutionFeedSse.cs')
    assert 'TimeSpan.FromSeconds(1)' in shared
    assert 'Last-Event-ID' in shared and 'ReplayCount' in shared and 'CommitOverlap' in shared
    assert 'Set<AdminSolutionFeedEntry>().AsNoTracking()' in shared
    for owner, base, context, name in [
        ('solutions', 'services/solutions/api', 'SolutionsDbContext', '/api/admin/solution-events'),
        ('tasks', 'services/tasks/assignment-api', 'TasksDbContext', '/api/admin/task-solution-events'),
    ]:
        db = body(f'{base}/Data/{context}.cs')
        migration = body(f'{base}/Migrations/20261119090000_AdminSolutionFeedOutbox.cs')
        assert 'CaptureAdminSolutionEvents();' in db
        assert 'AdminSolutionFeedEvents.Add(new AdminSolutionFeedEntry' in db
        assert 'SaveChangesAsync(bool acceptAllChangesOnSuccess' in db
        assert 'AdminSolutionFeedEvents' in migration and 'OccurredAtUtc' in migration
        assert 'CreateIndex' in migration
        assert 'AdminSolutionFeedEntry' in body(f'{base}/Migrations/{context}ModelSnapshot.cs')
        print('PASS:', owner, 'owns durable events and EF migration')
    route = body('services/solutions/api/Endpoints/Realtime/AdminSolutionEventsEndpoints.cs')
    task = body('services/tasks/assignment-api/Endpoints/Realtime/AdminTaskSolutionEventsEndpoints.cs')
    assert 'IsAdmin(http, cfg)' in route
    assert 'HasAnyRole(principal, "Admin")' in task
    js = body('apps/web/src/api/adminSolutionEvents.js')
    assert "'/api/admin/solution-events'" in js and "'/api/admin/task-solution-events'" in js
    assert 'states.every(Boolean)' in js, 'Do not claim LIVE if only one owner is connected'
    assert "['^/api/admin/task-solution-events$','tasks-api']" in body('tools/k3s/local/generate.mjs')
    assert not (root/'services/shared/Realtime/AdminSolutionEventPublisher.cs').exists()
    assert not (root/'services/solutions/api/Services/Realtime/AdminSolutionEventBroker.cs').exists()
    assert not (root/'services/education/api/Services/CourseMaps/CourseMapPresenceStore.cs').exists()
    editor = body('apps/web/src/features/course-assignments/components/CourseFlowEditor.jsx')
    for legacy in ('PresenceChanged', 'course-map-presence', "'Heartbeat'", 'setPresence(', 'Редакторов в карте'):
        assert legacy not in editor, f'Online map presence is still active: {legacy}'
    print('PASS: two secure DB-backed SSE feeds, one-second poll, no Redis events or map presence')
except Exception as error:
    print('FAIL: administrative YugabyteDB feed:', error, file=sys.stderr)
    sys.exit(1)
