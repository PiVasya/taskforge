#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

def text(rel):
    return (ROOT / rel).read_text(encoding='utf-8')

agent_ui = text('apps/web/src/features/agent/AgentFeature.jsx')
agent_model = text('apps/web/src/features/agent/agentModel.js')
agent_api = text('services/ai/api/Endpoints/Agent/AgentEndpoints.cs')
editor = text('apps/web/src/features/sql-task/SqlTaskEditor.jsx')
sql_model = text('apps/web/src/features/sql-task/sqlModel.js')
tiptap_editor = text('apps/web/src/components/tiptap/StatementEditor.jsx')
tiptap_viewer = text('apps/web/src/components/tiptap/StatementViewer.jsx')
worker = text('services/execution/sql-worker/cmd/sql-worker/main.go')
app = text('apps/web/src/App.jsx')

assert 'title: buildConversationTitle(value)' in agent_ui
assert 'maxLength = 160' in agent_model
assert 'MaxConversationTitleLength = 300' in agent_api
assert 'Title = NormalizeConversationTitle(title)' in agent_api
assert 'Title = string.IsNullOrWhiteSpace(title)' not in agent_api
assert '@tiptap/extension-underline' not in tiptap_editor
assert '@tiptap/extension-underline' not in tiptap_viewer
assert 'void refreshDatasetCatalogBestEffort(api.sqlDatasets' in editor
assert 'setDatasets(await api.sqlDatasets())' not in editor
assert 'toggleEngineTargets(targets, id, checked)' in editor
assert 'export async function refreshDatasetCatalogBestEffort' in sql_model
assert 'export function toggleEngineTargets' in sql_model
assert 'case "health", "ready":' in worker
assert '<Route path="/admin/ai/assistant" element={<AgentPage />} />' in app
print('Authoring regression invariants OK')
