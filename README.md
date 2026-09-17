# AI Work Orchestrator

An AI employee runtime: it receives work from Slack, assembles organizational
context, plans the change, waits for a human to confirm the plan, has a coding
agent (Claude or Codex) implement it, opens one pull request per changed
repository, follows review feedback through to approval, and curates durable
lessons into Obsidian when the task completes.

The full design is in [`PROJECT_PLAN.md`](./PROJECT_PLAN.md). In short: a task
moves through a small state machine (`Received` → `Planning` →
`WaitingForPlanConfirmation` → `Ready`/`Running` → `Verifying` →
`WatchingPullRequests`/`AddressingFeedback` → `Completed`), with every state
transition, step, approval, and tool call durably recorded so any outcome can be
traced back to the request that caused it.

## Local development quickstart

```bash
# 1. Database
docker compose -f deploy/docker-compose.yml up -d postgres

# 2. Apply migrations
export ORCHESTRATOR_CONNECTION_STRING="Host=localhost;Database=orchestrator;Username=orchestrator;Password=orchestrator"
dotnet ef database update \
  --project src/Orchestrator.Infrastructure \
  --startup-project src/Orchestrator.Api

# 3. Provision yourself an admin account (reads the password from stdin - see
#    docs/runbooks/provision-administrator.md for why)
printf '%s' 'a long passphrase' | dotnet run --project src/Orchestrator.Cli -- provision-admin --login <you>

# 4. Run the API host
dotnet run --project src/Orchestrator.Api

# 5. Run the admin frontend
cd admin && npm install && npm run dev
```

Slack and Azure DevOps integration, and the coding-agent sandbox, need real
secrets and a bit more setup - see [`deploy/README.md`](./deploy/README.md) for
the full secret list and the Docker Compose deployment, and
[`docs/runbooks/local-docker-setup.md`](./docs/runbooks/local-docker-setup.md)
if you are developing on a machine where the Docker CLI is not directly on
`PATH` (this repository's own dev environment is one such machine).

## Running the tests

```bash
dotnet test Orchestrator.sln
```

Most of the suite (`Orchestrator.Integration.Tests`, `Orchestrator.Acceptance.Tests`)
needs a reachable Docker daemon (Testcontainers for Postgres, and for the
acceptance suite's sandboxed-agent tests). See
[`docs/runbooks/local-docker-setup.md`](./docs/runbooks/local-docker-setup.md)
if `dotnet test` reports Docker as unreachable.

## Operator runbooks

Day-two operations live under [`docs/runbooks/`](./docs/runbooks/):

- [`restart-and-recovery.md`](./docs/runbooks/restart-and-recovery.md) - restart order, confirming the outbox drained, reading lease-reclaim results.
- [`cancel-a-task.md`](./docs/runbooks/cancel-a-task.md) - what cancellation does and does not undo, handling an open PR afterwards.
- [`revoke-authorization.md`](./docs/runbooks/revoke-authorization.md) - revoking a task's authorization and confirming the next tool call is denied.
- [`reconcile-uncertain-operations.md`](./docs/runbooks/reconcile-uncertain-operations.md) - finding `Unknown` tool-call outcomes and why retry is refused until they are reconciled.
- [`backup-and-restore.md`](./docs/runbooks/backup-and-restore.md) - what to back up (database, artifacts, vault revisions), what is deliberately not backed up, and restore order.
- [`rotate-secrets.md`](./docs/runbooks/rotate-secrets.md) - rotating each secret and which services need to restart.
- [`provision-administrator.md`](./docs/runbooks/provision-administrator.md) - creating the first (and every) admin account.
- [`azure-devops-service-hooks.md`](./docs/runbooks/azure-devops-service-hooks.md) - wiring up the Azure DevOps webhook subscription.
- [`local-docker-setup.md`](./docs/runbooks/local-docker-setup.md) - working around a Windows dev environment with no native `docker` CLI.

## Deployment

[`deploy/`](./deploy/) has the Docker Compose definition (Postgres, the API
host, the sandbox worker image, and the egress-restricted network the sandbox
runs in) and the secrets it needs before first start - see
[`deploy/README.md`](./deploy/README.md).
