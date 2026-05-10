# Workflow map

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
