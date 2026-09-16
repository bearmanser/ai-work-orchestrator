# Provisioning an admin account

There is no self-service registration endpoint anywhere in this system. An admin
account exists only because an operator ran the `provision-admin` CLI command
against the database. Task 12's login endpoint authenticates against accounts
created this way and only this way.

## 1. Start the database

```bash
docker compose up -d postgres
```

(Or however Postgres is run in your environment - any reachable Postgres 16+
instance works.)

## 2. Apply migrations

```bash
export ORCHESTRATOR_CONNECTION_STRING="Host=localhost;Database=orchestrator;Username=postgres;Password=postgres"
dotnet ef database update \
  --project src/Orchestrator.Infrastructure \
  --startup-project src/Orchestrator.Infrastructure
```

This creates the `orchestrator.admin_accounts` table (among others) if it does not
already exist.

## 3. Provision the account

The password is **never** passed as a command-line argument - `argv` is visible to
other users on the same host via the process table (`ps`, `/proc/<pid>/cmdline`)
and would be captured in shell history. Instead, pipe it into the CLI's stdin:

```bash
ADMIN_PASSWORD='choose a long passphrase, at least 12 characters'
printf '%s' "$ADMIN_PASSWORD" | dotnet run --project src/Orchestrator.Cli -- provision-admin --login magnus
unset ADMIN_PASSWORD
```

Notes:

- `--login` is lowercased and trimmed on write (`Magnus` and `magnus ` both become
  `magnus`), and login lookups on the login path are case-insensitive.
- The password must be at least 12 characters; shorter passwords are rejected
  before anything is hashed.
- Provisioning a login name that already exists fails loudly (an exception) rather
  than silently overwriting the existing account.
- The command writes exactly one `AdminAccount` row and one audit event
  (`admin.provisioned`) - nothing else observes or logs the plaintext password.

## There is no other way to create an account

If a request or a ticket asks for a "sign up" or "register" endpoint for admin
users: there isn't one, and per the product decision behind this task, there
should not be one. Every admin account traces back to an operator running this
command.
