# Deploying the orchestrator

This directory holds the deployable definition of the orchestrator: `docker-compose.yml`
(five services), the sandbox worker image (`worker.Dockerfile`), the API image
(`api.Dockerfile`), and the egress allowlist the sandbox network is restricted to.

## Services

| Service | What it is |
|---|---|
| `postgres` | Postgres 16, the orchestrator's only database. Named volume `postgres-data`, healthcheck via `pg_isready`. |
| `api` | The ASP.NET host (`Orchestrator.Api`), which also runs the Hangfire server in-process - there is no separate worker process. Spawns one throwaway sandbox container per agent invocation via the host's Docker daemon (`SandboxedProcessRunner`). |
| `worker-image-builder` | One-shot build of `orchestrator/worker:latest` (the sandbox image agent invocations run in). Exits immediately by design; `api` depends on it completing successfully. |
| `egress-proxy` | tinyproxy, the only route out of the `orchestrator-agent` network. Allowlist in `egress-allowlist.conf` / `egress-hosts.list`. |
| `orchestrator-agent` (network) | Internal-only network every sandbox container joins. `api` itself is **not** on it - it talks to sandbox containers through the mounted `docker.sock`, not over the network. |

## Secrets that must exist before first start

Every secret below is read through `ISecretStore` (`EnvironmentSecretStore`), which maps a
`SecretRef("some.key")` to the environment variable `ORCHESTRATOR_SECRET_SOME_KEY` (dots
become underscores, uppercased). `docker-compose.yml`'s `api` service takes each one as a
Docker secret file under `./secrets/<name>` and exports it into that variable at container
start (see the service's `entrypoint`). Create the directory and files before `docker compose
up`:

```bash
mkdir -p deploy/secrets
chmod 700 deploy/secrets
```

| File (under `deploy/secrets/`) | Used by | Notes |
|---|---|---|
| `orchestrator.database-connection` | `Program.cs`'s `ORCHESTRATOR_CONNECTION_STRING` fallback | Not a `SecretRef` - read directly. A full Npgsql connection string, e.g. `Host=postgres;Database=orchestrator;Username=orchestrator;Password=...`. |
| `slack.signing-secret` | `SlackWebhookEndpoint`, `SlackInteractiveEndpoint` | Slack app's signing secret, verifies inbound webhook signatures. |
| `slack.bot-token` | `SlackChatGateway` | Bot token used to post to `chat.postMessage`. |
| `azuredevops.pat` | `AzureDevOpsHttpClient` (via `AzureDevOpsOptions.PatSecret`) | Personal access token with Code (read/write) and Pull Request scopes. |
| `azuredevops.webhook-password` | `AzureDevOpsWebhookEndpoint` | HTTP Basic password the Azure DevOps service hook subscription authenticates with. |
| `anthropic.api-key` | `ClaudeCliProvider` | Passed into the sandbox container as `ANTHROPIC_API_KEY`. |
| `openai.api-key` | `CodexCliProvider` | Passed into the sandbox container as `OPENAI_API_KEY`. |
| `p1.staging.connection` | `NamedQueryDomainDataSource` (P1 checks) | Connection string for the `staging` P1 environment named in `config/p1-named-queries.json`. |

One secret is deliberately **not** a fixed file: `git.<repository-name>.credential` (for
example `git.backend.credential`), used by `LibGit2Workspace` and
`PullRequestCreationStepHandler` to push each repository's work branch. Its key is per
repository, not fixed, so it is provisioned through `POST /api/admin/integrations` (reference
only - the endpoint stores which `SecretRef` a repository uses, not the secret value itself)
once the repository is registered, and its value still needs to exist as
`ORCHESTRATOR_SECRET_GIT_<REPO>_CREDENTIAL` wherever the `api` container actually reads secrets
from in your environment (compose here exports the fixed list above; add this one alongside it
per repository before that repository's first push).

## Also required before first start

- `GIT_ROOT` and `VAULT_ROOT` environment variables (referenced by `docker-compose.yml`,
  e.g. in a `.env` file next to it): absolute **host** paths, identical inside and outside the
  `api` container (see the compose file's comments on why - the container drives the host's own
  Docker daemon through the mounted socket, so any path it hands to `docker run -v` must resolve
  on the host). They default to `/srv/orchestrator/git` and `/srv/orchestrator/vault` so that
  bringing up an unrelated service (for example just `postgres`, or this repository's own
  sandbox integration tests, which only start `egress-proxy`) never fails for lack of them - but
  a real deployment should set both explicitly rather than relying on the default.
  ```bash
  mkdir -p /srv/orchestrator/git /srv/orchestrator/vault
  echo "GIT_ROOT=/srv/orchestrator/git" >> deploy/.env
  echo "VAULT_ROOT=/srv/orchestrator/vault" >> deploy/.env
  ```
- The sandbox image: `docker compose -f deploy/docker-compose.yml build worker-image-builder`
  (or just `docker compose ... up`, which builds it as a dependency).
- Database migrations, once `postgres` is up: see
  [`../docs/runbooks/restart-and-recovery.md`](../docs/runbooks/restart-and-recovery.md) for the
  exact `dotnet ef database update` invocation.
- At least one admin account: `dotnet run --project src/Orchestrator.Cli -- provision-admin
  --login <name>` (reads the password from stdin), against the same
  `ORCHESTRATOR_CONNECTION_STRING` the `api` service uses.

## Starting it

```bash
docker compose -f deploy/docker-compose.yml up -d postgres
# wait for postgres to report healthy, then:
docker compose -f deploy/docker-compose.yml up -d
```

See [`../README.md`](../README.md) for the local (non-Docker) development quickstart, and
[`../docs/runbooks/`](../docs/runbooks/) for restart, cancellation, revocation, reconciliation,
backup and secret-rotation procedures.
