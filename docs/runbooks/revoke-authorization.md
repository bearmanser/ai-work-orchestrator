# Revoking a task's authorization

Use this when a task should stop being able to touch repositories, restricted
tools or P1 environments immediately - without necessarily cancelling the task
itself (see [`cancel-a-task.md`](./cancel-a-task.md) if the task should also
stop entirely).

## 1. Revoke it

```bash
curl -X POST http://localhost:8080/api/admin/tasks/{taskId}/revoke-authorization \
  -H "Cookie: <admin session cookie>"
```

(`TaskOperationsEndpoints.RevokeAuthorization`, `AdminOnly` policy.) Returns
`204 No Content`, or `404` if the task does not exist.

## 2. What this does

`OperationalControlService.RevokeAuthorizationAsync` marks the task's
`AuthorizationEnvelope` revoked. Every restricted operation in this system goes
through `PolicyEngine`/`IToolGateway`, which check the envelope before allowing
a tool call - a revoked envelope fails that check for every kind of action:
repository writes, restricted-action approval, provider invocation, P1 query
execution.

## 3. Confirm the next tool call is actually denied

Trigger any pending step for the task (or wait for it to run on the normal
schedule), then check the audit trail for a `Denied` outcome on a
`policy.evaluate` entry:

```sql
SELECT actor, action, reason, outcome, occurred_at
FROM orchestrator.audit_events
WHERE task_id = '{taskId}' AND action = 'policy.evaluate'
ORDER BY occurred_at DESC
LIMIT 5;
```

Expect `outcome = 'Denied'` for anything attempted after the revocation
timestamp. This is the same check
`ADisallowedProviderEnvironmentOrToolIsBlockedBeforeExecution` exercises in
`EndToEndWorkflowTests` - a revoked/denied envelope must show up here, not just
fail silently.

If the task has no pending step to trigger this check against, and you need to
confirm the deny takes effect without waiting, re-run the task's next step
manually through the retry endpoint (see
[`reconcile-uncertain-operations.md`](./reconcile-uncertain-operations.md) for
the retry endpoint's shape) and check the same query afterwards.

## 4. Revocation is permanent for that envelope

There is no "re-authorize" endpoint - `AuthorizationEnvelope` is not designed to
be un-revoked. If the task should continue, it needs a fresh envelope, which in
this system only happens by way of a new task (or, if a future task supports it,
a fresh plan confirmation cycle - check `AuthorizationEnvelope`'s current
constructors before assuming this exists for a given task's state).
