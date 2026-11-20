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
    # One physically durable outbox, owned by Solutions for all five kinds.
    db = body('services/solutions/api/Data/SolutionsDbContext.cs')
    migration = body('services/solutions/api/Migrations/20261119090000_AdminSolutionFeedOutbox.cs')
    assert 'CaptureAdminSolutionEvents();' in db
    assert 'CaptureInteractiveAttemptEvents();' in db
    assert 'AdminSolutionFeedEvents.Add(new AdminSolutionFeedEntry' in db
    assert 'SaveChangesAsync(bool acceptAllChangesOnSuccess' in db
    assert 'AdminSolutionFeedEvents' in migration and 'OccurredAtUtc' in migration
    assert 'AdminSolutionFeedEntry' in body('services/solutions/api/Migrations/SolutionsDbContextModelSnapshot.cs')
    assert 'AdminSolutionFeedEvents' not in body('services/tasks/assignment-api/Data/TasksDbContext.cs')
    route = body('services/solutions/api/Endpoints/Realtime/AdminSolutionEventsEndpoints.cs')
    assert 'IsAdmin(http, cfg)' in route
    assert 'MapGet("/api/admin/task-solution-events", Stream)' in route
    js = body('apps/web/src/api/adminSolutionEvents.js')
    assert "const FEED_PATHS = ['/api/admin/solution-events'];" in js
    assert "['^/api/admin/task-solution-events$','solutions-api']" in body('tools/k3s/local/generate.mjs')
    assert 'location = /api/admin/task-solution-events' in body('apps/gateway/snippets/api-routes.conf')
    assert not (root/'services/tasks/assignment-api/Endpoints/Realtime/AdminTaskSolutionEventsEndpoints.cs').exists()
    assert not (root/'services/shared/Realtime/AdminSolutionEventPublisher.cs').exists()
    assert not (root/'services/solutions/api/Services/Realtime/AdminSolutionEventBroker.cs').exists()
    assert not (root/'services/education/api/Services/CourseMaps/CourseMapPresenceStore.cs').exists()
    editor = body('apps/web/src/features/course-assignments/components/CourseFlowEditor.jsx')
    for legacy in ('PresenceChanged', 'course-map-presence', "'Heartbeat'", 'setPresence(', 'Редакторов в карте'):
        assert legacy not in editor, f'Online map presence is still active: {legacy}'
    print('PASS: one secure Solutions DB-backed SSE feed, one-second poll, no Redis events or map presence')
except Exception as error:
    print('FAIL: administrative YugabyteDB feed:', error, file=sys.stderr)
    sys.exit(1)
