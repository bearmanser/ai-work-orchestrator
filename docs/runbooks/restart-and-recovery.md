# Restart and recovery

What to do after the `api` process (or its host) restarts - planned or not - and how
to confirm the system caught back up on its own.

## 1. Bring the database up first, then the API

```bash
docker compose -f deploy/docker-compose.yml up -d postgres
# wait for postgres to report healthy:
docker compose -f deploy/docker-compose.yml ps postgres
docker compose -f deploy/docker-compose.yml up -d api
```

If this is the very first start on a fresh database, apply migrations before
starting `api`:

```bash
export ORCHESTRATOR_CONNECTION_STRING="Host=localhost;Database=orchestrator;Username=orchestrator;Password=orchestrator"
dotnet ef database update \
  --project src/Orchestrator.Infrastructure \
  --startup-project src/Orchestrator.Api
```

## 2. Confirm the host actually came up

```bash
curl -sf http://localhost:8080/ && echo OK
```

Nothing in this codebase resumes a task by replaying in-memory state: every task,
step, outbox message and lease lives in Postgres, so a restart's only real risk is
work that was in flight the moment the process died. The rest of this runbook is
about confirming that work resumed correctly, not about restoring anything by hand.

## 3. Confirm the outbox drained (nothing stuck as unprocessed)

```sql
SELECT message_type, count(*)
FROM orchestrator.outbox_messages
WHERE processed_at IS NULL
GROUP BY message_type;
```

Hangfire's own recurring job (`outbox-dispatch`, every 5 seconds - see
`Program.cs`'s `RecurringJob.AddOrUpdate<OutboxPollingJob>`) drains this
automatically once the host is back up. An empty result a few seconds after
startup means it caught up; a row that keeps growing usually means the *consumer*
of that message type is failing - check `last_error` on the row:

```sql
SELECT id, message_type, attempt_count, last_error, next_attempt_at
FROM orchestrator.outbox_messages
WHERE processed_at IS NULL AND attempt_count > 0
ORDER BY occurred_at;
```

## 4. Read the lease-reclaim results

A step that was `Leased` when the process died is not automatically retried
until its lease expires. `LeaseReclaimJob` runs every minute (Hangfire recurring
job id `lease-reclaim`, see `Program.cs`) and puts any step whose
`lease_expires_at` is in the past back to `Pending`, where the normal step
pipeline (`StepExecutionJob`, dispatched by the outbox drain above) picks it up
again. To force a reclaim pass immediately instead of waiting up to a minute,
open the Hangfire dashboard (admin-only) and trigger the job:

```
http://localhost:8080/hangfire/recurring
```

then "Trigger now" on `lease-reclaim`. To read the result directly instead:

```sql
-- Steps a reclaim pass would still catch: leased, with an expired lease.
SELECT id, task_id, capability, lease_owner, lease_expires_at
FROM orchestrator.steps
WHERE status = 'Leased' AND lease_expires_at < now();

-- Steps already reclaimed and now waiting to run again.
SELECT id, task_id, capability, attempt_count, status
FROM orchestrator.steps
WHERE status = 'Pending' AND attempt_count > 0;
```

`attempt_count > 0` on a `Pending` step is the signature of a reclaimed
(previously leased-and-abandoned) step, as opposed to one that has simply never
run yet.

## 5. Duplicate delivery is expected and handled, not a bug to fix

A restart mid-delivery can mean Slack or Azure DevOps redelivers a webhook, or
Hangfire re-enqueues a job it never heard finish. Both are deduplicated already:

- Webhooks: `IEventRegistry.TryRegisterAsync` (`EventRegistry`, table
  `orchestrator.inbound_events`) - a redelivered event still returns 200 but is
  never processed twice. Confirm with:
  ```sql
  SELECT source, external_event_id, count(*) FROM orchestrator.inbound_events
  GROUP BY source, external_event_id HAVING count(*) > 1;
  ```
  (should always return zero rows - `(source, external_event_id)` has a unique
  index, so this query returning anything would mean the constraint itself
  failed, not that dedup silently let a duplicate through).
- Steps: `ILeaseManager`'s atomic compare-and-swap means two workers racing the
  same step id, one always loses the lease acquisition.

If a task looks like it progressed twice (e.g. two pull requests for one repository
change), that is a real defect - open one against `PullRequestCreationStepHandler`,
which is specifically designed to adopt an already-open PR for the same branch
rather than create a second one - do not work around it by hand.

## 6. Task state itself needs no recovery step

`WorkTask.State` and every `Step.Status` are read from Postgres on every access -
there is no separate cache to invalidate or warm. A task sitting in
`WaitingForPlanConfirmation`, `WaitingForHuman`, or `WaitingForActionApproval`
after a restart is not stuck: those are legitimate idle states waiting on a human,
and picking back up is exactly what steps 3-4 above already confirmed.
