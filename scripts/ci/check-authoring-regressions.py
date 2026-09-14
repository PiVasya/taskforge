#!/usr/bin/env python3
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]

def text(rel):
    return (ROOT / rel).read_text(encoding='utf-8')

agent_ui = text('apps/web/src/features/agent/AgentFeature.jsx')
agent_model = text('apps/web/src/features/agent/agentModel.js')
agent_api = text('services/ai/api/Endpoints/Agent/AgentEndpoints.cs')
editor = text('apps/web/src/features/sql-task/SqlTaskEditor.jsx')
sql_solve = text('apps/web/src/features/sql-task/SqlTaskSolve.jsx')
sql_db_viewer = text('apps/web/src/features/sql-task/SqlDatabaseViewer.jsx')
sql_model = text('apps/web/src/features/sql-task/sqlModel.js')
tiptap_editor = text('apps/web/src/components/tiptap/StatementEditor.jsx')
tiptap_viewer = text('apps/web/src/components/tiptap/StatementViewer.jsx')
worker = text('services/execution/sql-worker/cmd/sql-worker/main.go')
app = text('apps/web/src/App.jsx')
course_card = text('apps/web/src/features/course-assignments/components/CourseContentCard.jsx')
test_editor = text('apps/web/src/pages/TaskTestEditor.jsx')
test_solve = text('apps/web/src/pages/TaskTestSolve.jsx')
math_editor = text('apps/web/src/pages/MathTaskEditor.jsx')
math_solve = text('apps/web/src/pages/MathTaskSolve.jsx')
task_serialization = text('services/tasks/assignment-api/Services/Serialization/AssignmentApiSerializationService.cs')
test_runtime = text('services/tasks/assignment-api/Services/Testing/AssignmentApiTestingService.cs')
math_runtime = text('services/tasks/assignment-api/Services/Math/AssignmentApiMathService.cs')
task_graph = text('apps/web/src/features/course-assignments/courseTaskGraphJson.js')
auth_context = text('apps/web/src/auth/AuthContext.jsx')
private_browser_state = text('apps/web/src/auth/privateBrowserState.js')
solve_draft_store = text('apps/web/src/features/assignment-solve/solveDraftStore.js')

assert 'title: buildConversationTitle(value)' in agent_ui
assert 'maxLength = 160' in agent_model
assert 'MaxConversationTitleLength = 300' in agent_api
assert 'Title = NormalizeConversationTitle(title)' in agent_api
assert 'Title = string.IsNullOrWhiteSpace(title)' not in agent_api
assert '@tiptap/extension-underline' not in tiptap_editor
assert '@tiptap/extension-underline' not in tiptap_viewer
assert 'void refreshDatasetCatalogBestEffort(api.sqlDatasets' in editor
assert 'setDatasets(await api.sqlDatasets())' not in editor
assert re.search(
    r'changeSpec\(\{\s*targets:\s*toggleEngineTargets\(\s*targets\s*,\s*[A-Za-z_$][A-Za-z0-9_$]*\s*,\s*checked\s*\)\s*\}\s*\)',
    editor,
), 'SqlTaskEditor must route engine-target changes through toggleEngineTargets(targets, <profile>, checked)'
assert 'export async function refreshDatasetCatalogBestEffort' in sql_model
assert 'export function toggleEngineTargets' in sql_model
assert 'export function validationReadyForTargets' in sql_model
assert 'validationReadyForTargets(targets, view?.validation)' in editor
assert 'Написать SQL' in sql_solve
assert 'spec.targets.length > 1' in sql_solve
assert 'SolveActionDock' in sql_solve
assert 'sqlRuntime' not in sql_solve
assert 'Доступен' not in sql_solve and 'Недоступен' not in sql_solve
assert '/database' in sql_solve
assert "from '@tanstack/react-table'" in sql_db_viewer
assert '<Route path="/assignment/:assignmentId/database" element={<SqlDatabasePage />} />' in app
assert 'const latest = await api.sqlEdit(assignmentId);' in editor
assert 'case "health", "ready":' in worker
assert '<Route path="/admin/ai/assistant" element={<AgentPage />} />' in app
assert "assignment.lifecycleStatus !== 'published' && !assignment.isHidden" in course_card

assert 'data-taskforge-automation-id="test-unlimited-attempts"' in test_editor
assert 'Бесконечные попытки' in test_editor
assert 'disabled={!!s.unlimitedAttempts}' in test_editor
assert 'attemptsAreUnlimited(startData)' in test_solve
assert "? '∞' : startData.maxAttempts" in test_solve
assert 'data-taskforge-automation-id="math-unlimited-attempts"' in math_editor
assert 'attemptsAreUnlimited(startData)' in math_solve
assert 'Bool(settings, "unlimitedAttempts", false)' in task_serialization
assert 'spec.Settings.UnlimitedAttempts || HasUnlimitedAiTaskAttempts' in test_runtime
assert 'spec.Settings.UnlimitedAttempts || HasUnlimitedAiTaskAttempts' in math_runtime
assert 'unlimitedAttempts, passPercent' in test_runtime
assert 'unlimitedAttempts, passPercent' in math_runtime
assert "typeof value.unlimitedAttempts !== 'boolean'" in task_graph
assert "import { clearPrivateBrowserState } from './privateBrowserState';" in auth_context
assert 'clearPrivateBrowserState();' in auth_context
assert "'solve-draft:v'" in private_browser_state
assert "'results:'" in private_browser_state
assert "'image-results:'" in private_browser_state
assert "'taskforge-sql:'" in private_browser_state
assert "'taskforge.compiler.draft.v1.'" in private_browser_state
assert 'discardAllSolveDraftStores();' in private_browser_state
assert 'export function discardAllSolveDraftStores()' in solve_draft_store

print('Authoring regression invariants OK')
