# Cancelling a task

## 1. Cancel it

```bash
curl -X POST http://localhost:8080/api/admin/tasks/{taskId}/cancel \
  -H "Cookie: <admin session cookie>"
```

(`TaskOperationsEndpoints.CancelTask`, `AdminOnly` policy - sign in first via
`POST /api/auth/login`.) Returns `204 No Content` on success, `404` if the task
id does not exist.

## 2. What this does

- `OperationalControlService.CancelTaskAsync` fires `TaskTrigger.Cancelled` on the
  task. `TaskStateMachine.TryTransition` treats `Cancelled` specially - it is
  accepted from *any* non-terminal state, unlike every other trigger, which only
  fires from the specific states listed in the state map.
- `StepExecutionJob.CancelTaskAsync` cancels every `Pending` or `Leased` step for
  that task (`Step.Cancel()` - status becomes `Cancelled`, any lease is released).
- An audit entry is recorded (`Action = "task.cancelled"`, or the actor-driven
  equivalent from this endpoint).

## 3. What this does NOT undo

- **A pull request already opened stays open.** Cancellation does not close, abandon,
  or comment on any pull request `PullRequestCreationStepHandler` created. If the
  work should not land, close or abandon the PR yourself in Azure DevOps - the
  orchestrator will not touch it again once the task is cancelled (it only acts on
  PRs for tasks still in `WatchingPullRequests`/`AddressingFeedback`).
- **Money and time already spent are not refunded.** `BudgetLedger` reflects the
  cost of every agent invocation that already ran; cancelling stops future spend,
  it does not zero out the ledger.
- **A workspace already checked out on disk is not deleted.** `IGitWorkspace`
  handles are left as-is; cleaning up `GIT_ROOT` disk usage is a separate,
  unrelated concern (not something this task defines a runbook for).
- **A step already `Leased` by a live worker is not interrupted mid-flight.** Only
  `Pending`/`Leased` rows in the database are marked `Cancelled`; a coding-agent
  container that is still running when you cancel keeps running until it finishes
  or times out. Its eventual outcome is simply discarded - `StepExecutionJob.RunAsync`
  checks the step's status is still `Pending`/`Leased` before dispatching, and a
  `Cancelled` step is neither, so no next step is ever created for it once
  `NextStepScheduler` looks at the outcome.

## 4. If there is an open pull request afterwards

1. Decide whether the work is worth keeping.
   - **Keep it:** merge or leave the PR open as a normal PR - it is no longer
     managed by the orchestrator (the task that owned it is terminal), so route
     any further review normally, outside the orchestrator.
   - **Discard it:** abandon the PR directly in Azure DevOps. Nothing in the
     orchestrator does this automatically.
2. Check for orphaned tracking data (harmless, but useful to know it is there):
   ```sql
   SELECT pull_request_id, repository_id, url
   FROM orchestrator.pull_request_tracking
   WHERE task_id = '{taskId}';
   ```
3. If the task's git workspace needs reclaiming, that is a filesystem operation
   under `GIT_ROOT`, not an API call - see
   [`backup-and-restore.md`](./backup-and-restore.md) for what is and is not
   backed up under that root.
