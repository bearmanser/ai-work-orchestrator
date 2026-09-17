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

// The types below mirror Orchestrator.Api.Endpoints.RepositoryEndpoints and
// RelationshipEndpoints (src/Orchestrator.Api/Endpoints/RepositoryEndpoints.cs,
// RelationshipEndpoints.cs). Keep names and shapes identical to the C# records
// and update this file whenever those endpoints' DTOs change.

/** Mirrors Orchestrator.Domain.Repositories.BuildCommands. TimeSpan serializes as "hh:mm:ss". */
export interface BuildCommands {
  restore: string | null;
  build: string | null;
  test: string | null;
  lint: string | null;
  workingDirectory: string;
  timeout: string;
}

/** GET /api/admin/repositories, GET/PUT /api/admin/repositories/{id} — mirrors RepositoryProfileDto. */
export interface RepositoryProfile {
  id: string;
  name: string;
  remoteUrl: string;
  defaultBranch: string;
  purpose: string;
  ownerContactId: string;
  accessPolicy: string;
  buildCommands: BuildCommands;
  instructionFilePaths: string[];
  lastInspectedCommit: string | null;
  lastInspectedAt: string | null;
  version: number;
}

/** POST /api/admin/repositories body — mirrors RepositoryCatalogService.CreateRepositoryRequest. */
export interface CreateRepositoryRequest {
  name: string;
  remoteUrl: string;
  defaultBranch: string;
  purpose: string;
  ownerContactId: string;
  accessPolicy: string;
  buildCommands: BuildCommands;
  instructionFilePaths: string[];
}

/**
 * PUT /api/admin/repositories/{id} body — mirrors
 * RepositoryCatalogService.UpdateRepositoryRequest. Note accessPolicy is not
 * settable through update; it is create-only in the backend contract.
 */
export interface UpdateRepositoryRequest {
  name: string;
  purpose: string;
  defaultBranch: string;
  ownerContactId: string;
  buildCommands: BuildCommands | null;
  instructionFilePaths: string[];
}

/**
 * Orchestrator.Domain.Repositories.RelationshipStatus. Unlike TaskState, the
 * relationship DTOs serialize this as its string name (`r.Status.ToString()`
 * in RelationshipDto.From), not a numeric ordinal.
 */
export type RelationshipStatus = "Evidenced" | "AdminAsserted" | "Conflicted";

/** Mirrors Orchestrator.Domain.Repositories.RelationshipEvidence. */
export interface RelationshipEvidence {
  sourcePath: string;
  commitSha: string;
  excerpt: string;
}

/**
 * GET /api/admin/repositories/{id}/relationships — mirrors RelationshipDto.
 * Note the real route is nested under the source repository, not a flat
 * /api/admin/relationships list endpoint.
 */
export interface RepositoryRelationship {
  id: string;
  sourceRepositoryId: string;
  targetRepositoryId: string;
  relationshipType: string;
  status: RelationshipStatus;
  evidence: RelationshipEvidence[];
  lastVerifiedAt: string;
  lastVerifiedCommit: string | null;
  adminNote: string | null;
}

/** POST /api/admin/repositories/{id}/relationships body — mirrors AssertRelationshipRequest. */
export interface AssertRelationshipRequest {
  targetRepositoryId: string;
  relationshipType: string;
  note?: string | null;
}
