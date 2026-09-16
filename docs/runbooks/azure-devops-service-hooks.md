# Azure DevOps service hooks

The orchestrator ingests pull request activity through Azure DevOps service
hooks at `POST /webhooks/azure-devops`. The endpoint only registers the event
and enqueues a `pullrequest.event` outbox message, then returns 200
immediately. No PR processing happens in the request: Azure DevOps disables a
subscription after repeated slow or failing deliveries, so the intake must stay
fast and must answer 200 even for duplicate deliveries and unknown event types.

## 1. Create the three subscriptions

In Azure DevOps, go to Project Settings → Service hooks → Create subscription
and create one subscription for each of these event types, all pointing at the
same consumer URL:

1. **Pull request updated** (`git.pullrequest.updated`) — PR created, pushed to,
   or voted on.
2. **Pull request merged** (`git.pullrequest.merged`) — PR completed.
3. **Pull request commented on**
   (`ms.vss-code.git-pullrequest-comment-event`) — a comment thread is opened on
   a PR. Its payload nests the PR under `resource.pullRequest.pullRequestId`
   (rather than `resource.pullRequestId`); the parser handles both shapes.

Consumer settings for each subscription:

- **Consumer:** Web Hooks.
- **URL:** `https://<orchestrator-host>/webhooks/azure-devops`.
- **HTTP headers:** none required.
- **Basic authentication:** any username; the password is the shared webhook
  secret described below. Azure DevOps sends it as an HTTP Basic
  `Authorization` header.

Any other event type delivered to the URL is acknowledged with 200 and ignored:
nothing is enqueued.

## 2. Set the webhook password

The password comes from the `azuredevops.webhook-password` secret, resolved
through the configured `ISecretStore`. For local development with
`EnvironmentSecretStore`, set the corresponding environment variable on the API
host:

```bash
export ORCHESTRATOR_SECRET_AZUREDEVOPS_WEBHOOK_PASSWORD='a long random value'
```

The endpoint compares the supplied password with
`CryptographicOperations.FixedTimeEquals`. A missing or wrong password is a
401 with an empty body; the secret value never appears in logs, responses, or
stored artifacts — only the `SecretRef` is ever persisted or described.

## 3. Verify delivery

1. In Azure DevOps, go to Project Settings → Service hooks → History.
2. Each delivery should show response code **200**. A redelivery of an already
   seen payload `id` is deduplicated (registered once via the inbox, enqueued
   once) and still answered 200 so the subscription stays enabled.
3. In the orchestrator database, confirm intake:
   - one `inbound_events` row with source `azuredevops` and the payload's `id`
     as the external event id;
   - one `outbox_messages` row with message type `pullrequest.event` whose
     payload carries the pull request id.
4. A `400` in History means the body was not valid JSON; a `401` means the
   subscription's Basic password does not match the
   `azuredevops.webhook-password` secret.
