# Rotating secrets

Every secret in this system is a `SecretRef` resolved through `ISecretStore`
(`EnvironmentSecretStore` in production: `SecretRef("some.key")` reads the
environment variable `ORCHESTRATOR_SECRET_SOME_KEY`). Nothing caches a secret
value beyond the lifetime of the request/job that read it - there is no
in-process cache to invalidate - so rotation is: replace the file, restart the
service(s) that read it.

| Secret | Rotate by | Restart |
|---|---|---|
| `orchestrator.database-connection` | Update the Postgres role's password, then the secret file. | `api` (reads it once, into `ORCHESTRATOR_CONNECTION_STRING`, at container start - see `deploy/docker-compose.yml`'s entrypoint). |
| `slack.signing-secret` | Regenerate in the Slack app's Basic Information page, update the secret file. | `api` (`SlackWebhookEndpoint`/`SlackInteractiveEndpoint` read it per request via `ISecretStore`, so no restart is strictly required, but restart anyway to fail fast if the file is wrong). |
| `slack.bot-token` | Reinstall/rotate the Slack app's bot token, update the secret file. | `api` (`SlackChatGateway`). |
| `azuredevops.pat` | Issue a new PAT with the same scopes (Code read/write, Pull Request), update the secret file, revoke the old PAT in Azure DevOps only after confirming the new one works. | `api` (`AzureDevOpsHttpClient`). |
| `azuredevops.webhook-password` | Change it in **both** places at once - the secret file, and the Azure DevOps service hook subscription's Basic Auth password (see `docs/runbooks/azure-devops-service-hooks.md`) - a mismatch here means every webhook delivery gets `401`ed until both sides agree. | `api` (`AzureDevOpsWebhookEndpoint`). |
| `anthropic.api-key` | Issue a new key in the Anthropic console, update the secret file. | `api` only needs it re-read for new invocations; already-running sandbox containers were handed the old value as `ANTHROPIC_API_KEY` at launch and are unaffected either way since they are short-lived and thrown away per invocation. |
| `openai.api-key` | Same as above, for Codex. | Same as above. |
| `p1.staging.connection` | Update the connection string, update the secret file. | `api` (`NamedQueryDomainDataSource`). |
| `git.<repository-name>.credential` | Per repository, not a fixed key - rotate the credential with whatever git host the repository lives on, then update wherever this repository's secret is actually sourced in your environment (see `deploy/README.md`'s note on why this one is not a fixed docker compose secret). | `api`. |

## Rotation checklist (any secret)

1. Generate/obtain the new value.
2. Write it to the secret file (`deploy/secrets/<name>` for the compose
   deployment - see `deploy/README.md`).
3. Restart `api`:
   ```bash
   docker compose -f deploy/docker-compose.yml restart api
   ```
4. Confirm the host came back up (see
   [`restart-and-recovery.md`](./restart-and-recovery.md)'s step 2).
5. Only after confirming the new value works, revoke/delete the old value at
   its source (Slack app settings, Azure DevOps PAT list, Anthropic/OpenAI
   console). Revoking first and rotating second turns a planned rotation into
   an outage if anything about the new value is wrong.
6. For `azuredevops.pat` and `anthropic.api-key`/`openai.api-key`, exercise one
   real operation that uses the new value before revoking the old one - a PR
   comment reply, or waiting for the next scheduled agent invocation - since
   these are checked out lazily and a typo would otherwise not surface until
   the next real usage.
