// Mirrors the server DTOs by hand. Keep the names identical to the C# records
// in src/Orchestrator.Api (e.g. LoginRequest in Auth/AuthEndpoints.cs) and
// update this file whenever an endpoint changes shape.

/** POST /api/auth/login body — mirrors AuthEndpoints.LoginRequest. */
export interface LoginRequest {
  loginName: string;
  password: string;
}

/** GET /api/auth/me response — mirrors the anonymous { loginName, isAdministrator } result. */
export interface SessionInfo {
  loginName: string;
  isAdministrator: boolean;
}

// The types below mirror Orchestrator.Api.Endpoints.TaskQueryEndpoints
// (src/Orchestrator.Api/Endpoints/TaskQueryEndpoints.cs). Keep names and
// shapes identical to the C# records and update this file whenever that
// endpoint's DTOs change.

/** GET /api/admin/tasks — mirrors PagedResult<TaskSummary>. */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/**
 * Orchestrator.Domain.Tasks.TaskState. The API has no JsonStringEnumConverter
 * configured, so the wire format is the numeric ordinal, not the name - this
 * mirrors the C# enum's declaration order exactly. Use `taskStateName` below
 * rather than comparing against these numbers directly in UI code.
 */
export enum TaskState {
  Received = 0,
  Planning = 1,
  WaitingForHuman = 2,
  WaitingForPlanConfirmation = 3,
  Ready = 4,
  Running = 5,
  WaitingForActionApproval = 6,
  Verifying = 7,
  WatchingPullRequests = 8,
  AddressingFeedback = 9,
  Suspended = 10,
  Completed = 11,
  Failed = 12,
  Cancelled = 13,
}

/** Human-readable label for a TaskState value. */
export function taskStateName(state: TaskState): string {
  return TaskState[state] ?? `Unknown(${state})`;
}

/** Mirrors TaskSummary — one row in the admin task list. */
export interface TaskSummary {
  id: string;
  objective: string;
  state: TaskState;
  requesterId: string;
  updatedAt: string;
  blockedOn: string | null;
  waitingFor: string | null;
}

/** Orchestrator.Domain.Steps.StepStatus - also wire-serialized as a numeric ordinal. */
export enum StepStatus {
  Pending = 0,
  Leased = 1,
  Succeeded = 2,
  Failed = 3,
  Cancelled = 4,
}

/** Human-readable label for a StepStatus value. */
export function stepStatusName(status: StepStatus): string {
  return StepStatus[status] ?? `Unknown(${status})`;
}

/** Mirrors StepSummary. */
export interface StepSummary {
  id: string;
  capability: string;
  status: StepStatus;
  createdAt: string;
}

/** Mirrors PullRequestSummary. Empty for every task today — see TaskDetailPage. */
export interface PullRequestSummary {
  pullRequestId: number;
  url: string;
  isRequired: boolean;
  isApproved: boolean;
  approvalReason: string | null;
}

/** Mirrors CheckResultSummary — a "local" (runtime) check. */
export interface CheckResultSummary {
  name: string;
  outcome: string;
  runAt: string;
}

/** Mirrors DomainCheckSummary — a "P1"/domain check. Never merged with CheckResultSummary. */
export interface DomainCheckSummary {
  name: string;
  outcome: string;
  runAt: string;
}

/** Mirrors PlanRevisionSummary. */
export interface PlanRevisionSummary {
  revision: number;
  confirmedAt: string;
}

/** Mirrors BudgetSummary. */
export interface BudgetSummary {
  maxUsd: number;
  spentUsd: number;
  maxWallClock: string;
  elapsedWallClock: string;
  maxAgentInvocations: number;
  agentInvocations: number;
  maxCorrectionAttempts: number;
  correctionAttempts: number;
}

/** Mirrors AuditEntrySummary. */
export interface AuditEntrySummary {
  id: string;
  actor: string;
  action: string;
  reason: string;
  outcome: string;
  occurredAt: string;
}

/** GET /api/admin/tasks/{id} — mirrors TaskDetail. */
export interface TaskDetail {
  id: string;
  objective: string;
  state: TaskState;
  requesterId: string;
  updatedAt: string;
  blockedOn: string | null;
  waitingFor: string | null;
  steps: StepSummary[];
  pullRequests: PullRequestSummary[];
  checks: CheckResultSummary[];
  domainChecks: DomainCheckSummary[];
  confirmedPlan: PlanRevisionSummary | null;
  budget: BudgetSummary;
  audit: AuditEntrySummary[];
}

/** Query params accepted by GET /api/admin/tasks. */
export interface TaskListQuery {
  state?: TaskState;
  repositoryId?: string;
  page?: number;
  pageSize?: number;
}
