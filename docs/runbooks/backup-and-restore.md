# Backup and restore

Three things hold state this system cannot regenerate on its own: the Postgres
database, the artifact root, and the vault's revision history. A fourth
directory (the vault's search index) is intentionally excluded.

## 1. Back up the database

```bash
docker compose -f deploy/docker-compose.yml exec postgres \
  pg_dump -U orchestrator -d orchestrator -F custom -f /tmp/orchestrator.dump
docker compose -f deploy/docker-compose.yml cp postgres:/tmp/orchestrator.dump ./backups/orchestrator-$(date +%Y%m%d%H%M%S).dump
```

This is everything transactional: tasks, steps, the outbox, plan revisions,
budgets, approvals, tool-call records, admin accounts, people and ownership,
policy and integration settings. If it lives in a table under the
`orchestrator` schema, `pg_dump` has it.

## 2. Back up the artifact root

```bash
tar czf ./backups/artifacts-$(date +%Y%m%d%H%M%S).tar.gz -C /data artifacts
```

(`FileSystemArtifactStore` - diffs, evidence bundles, transcripts. The path is
whatever `Orchestrator:Artifacts:Root` points at; `/data/artifacts` matches the
volume `docker-compose.yml`'s `api` service mounts.) Artifacts are referenced
from Postgres by id (`artifacts` table) but stored only on disk - losing this
directory without the database backup above, or vice versa, leaves dangling
references either way, so back both up together, not independently.

## 3. Back up the vault's revision directory

```bash
tar czf ./backups/vault-revisions-$(date +%Y%m%d%H%M%S).tar.gz \
  -C "$VAULT_ROOT" .orchestrator-revisions
```

(`FileSystemVaultStore` keeps every past version of a note under
`{vaultRoot}/.orchestrator-revisions/`, which is what
`MemoryCurator.RevertAsync` reads to undo a bad memory write. The notes
themselves, under the rest of `$VAULT_ROOT`, are the operator's own Obsidian
vault content - back that up the way you already back up the vault, this
runbook is only about the orchestrator-specific revision directory living
inside it.)

## 4. What is NOT backed up, and why

`{vaultRoot}/.orchestrator-index/` (or wherever `Orchestrator:Memory:IndexDirectory`
points, if set separately) is the keyword/frontmatter search index Task 41
builds over vault content. It is derived entirely from the notes and their
revisions - rebuildable from scratch by re-indexing the vault - so backing it up
buys nothing and is deliberately skipped.

## 5. Restore order

Restore in the reverse of a live system's dependency direction - files the
database references, before the database that references them:

1. **Artifacts and vault revisions first.** Untar both archives back to their
   original paths before touching the database, so nothing in Postgres ever
   points at a path that does not exist yet.
2. **Database last:**
   ```bash
   docker compose -f deploy/docker-compose.yml cp ./backups/orchestrator-<ts>.dump postgres:/tmp/orchestrator.dump
   docker compose -f deploy/docker-compose.yml exec postgres \
     pg_restore -U orchestrator -d orchestrator --clean --if-exists /tmp/orchestrator.dump
   ```
3. **Rebuild the search index** (skipped in the backup by design - see step 4):
   restart `api`; `FileSystemVaultStore`/the indexing job re-derives it from the
   restored vault content on its own schedule, or trigger it manually if an
   admin endpoint for that exists in your deployed version.
4. Start `api` and confirm with a task read (`GET /api/admin/tasks/{taskId}` for
   a task you know existed in the backup) that both state and its evidence
   artifacts resolve correctly.
