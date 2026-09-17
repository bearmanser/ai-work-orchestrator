# Reconciling uncertain (`Unknown`) operations

A tool call whose local record is stuck `Unknown` means the process crashed, or
the connection dropped, somewhere between "we are about to call Azure DevOps" and
"we recorded whether that call succeeded" - the remote side effect may or may not
have actually happened. This is exactly the case `PullRequestCreationStepHandler`'s
own remarks describe (`UnreconciledOperationException`), and it is why retrying
blindly is refused.

## 1. Find tasks with an unresolved `Unknown` outcome

```sql
SELECT id, task_id, step_id, action, idempotency_key, recorded_at
FROM orchestrator.tool_call_records
WHERE outcome = 0 -- ToolCallOutcome.Unknown (the enum's stored representation)
ORDER BY recorded_at;
```

## 2. Why retry is refused first

```bash
curl -X POST http://localhost:8080/api/admin/tasks/{taskId}/steps/{stepId}/retry \
  -H "Cookie: <admin session cookie>"
```

`OperationalControlService.RetryStepAsync` checks
`IToolCallStore.HasUnknownOutcomesAsync(taskId, ...)` **before** doing anything
else, and throws (surfaced by the endpoint as `409 Conflict`) if the task has
any `Unknown` tool call. This is deliberate: `Step.RetryReset`'s own remarks
explain the reasoning at the step level, and the same logic applies at the
tool-call level - retrying a `pull_request.create` step whose previous attempt is
`Unknown` risks calling Azure DevOps a second time for a PR that may already
exist. Reconcile first; only retry once reconciliation has resolved every
`Unknown` outcome for that task.

## 3. Run reconciliation

```bash
curl -X POST http://localhost:8080/api/admin/tasks/{taskId}/reconcile \
  -H "Cookie: <admin session cookie>"
```

(`TaskOperationsEndpoints.ReconcileTask`, backed by
`OperationReconciliationService` and, for pull-request creation specifically,
`PullRequestCreateReconciler` - the only `IOperationReconciler` registered today;
other `ActionKind`s have no reconciler yet.) The response distinguishes "checked
and it was fine" from "nobody checked":

```json
{
  "resolved": 1,
  "failed": 0,
  "stillUnknown": 0,
  "unresolvedTargets": [],
  "message": "Reconciled 1 succeeded and 0 failed operation(s)."
}
```

- `resolved` / `failed`: reconciliation found remote state and updated the local
  record accordingly (e.g. it found the open PR by branch name and marked the
  create call `Succeeded`, or confirmed it truly never happened and marked it
  `Failed`).
- `stillUnknown` > 0, with `unresolvedTargets` naming an `ActionKind`: no
  reconciler is registered for that kind of operation. This is a real gap to
  close (register an `IOperationReconciler` for it), not something to route
  around by retrying anyway.

## 4. Only then retry

Once `stillUnknown` is `0`, the retry endpoint from step 2 will no longer be
refused.
