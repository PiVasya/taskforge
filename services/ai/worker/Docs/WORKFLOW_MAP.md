# Workflow map

Current top-level coordinator:

```text
AdaptiveAgentLoopWorkflow
  inspect_context -> classify_request
  safe action / actions[] batch
  AgentLoopState persists observations and working memory
  delegate_* -> review_delegated_result -> finish
  load_editable_assignments -> map_course_structure
    -> analyze_assignment_complexity
    -> propose_assignment_patch_set -> review_patch_set -> finish
```

Specialized workflows delegated by the loop:

```text
OpenChatWorkflow
  LoadRunContextExecutor
  ChatClientAgent + tools
  ResultEnvelope

CourseAuditWorkflow
  LoadRunContextExecutor
  PlanRequestExecutor
  CourseAuditExecutor
  artifact: course_gap_audit

AssignmentDraftWorkflow
  LoadRunContextExecutor
  PlanRequestExecutor
  loop 0..MaxDraftRepairAttempts:
    DraftAuthorExecutor
    DraftValidationExecutor
    DraftCriticExecutor
  ApprovalGateExecutor
  artifacts: assignment_draft_ready, approval_request

PolishAssignmentDraftWorkflow
  LoadRunContextExecutor
  selectedTask from run.request
  loop 0..MaxDraftRepairAttempts:
    model polishes selected task
    DraftValidationExecutor
    DraftCriticExecutor
  ApprovalGateExecutor
  artifacts: polished_assignment_draft, approval_request

CourseEditWorkflow
  LoadRunContextExecutor
  PlanRequestExecutor
  agent prepares patch
  ApprovalGateExecutor
  artifacts: course_edit_proposal, approval_request
```
