# SimpleIPaaS operations runbook

## Health endpoints

- /health/live confirms that the API process is responding. It does not require the database.
- /health checks API database reachability and is suitable for the API container healthcheck.
- /health/ready checks the database and confirms that the standalone Engine has written a recent heartbeat.
- /metrics returns Prometheus text metrics. It requires `X-Metrics-Key` matching `Metrics:ApiKey` (the legacy `X-Api-Key` header is accepted only for this endpoint, using the same configured value). Set the key to at least 32 random characters in production.

The API and Engine must use the same SQLite file. If /health is healthy but /health/ready is unhealthy, inspect the Engine container first.

## Metrics and alerts

Import ops/prometheus/simpleipaas-alerts.yml into the Prometheus alerting rules configuration. Configure Prometheus to scrape the API /metrics endpoint with `X-Metrics-Key: <Metrics:ApiKey>`. The rules cover:

- Engine heartbeat loss.
- API 5xx responses.
- Failed flow executions.
- A growing queued-execution backlog.

The application does not send execution payloads or errors to an arbitrary alert URL. Route alerts through the organization’s existing Prometheus/Alertmanager policy.

## SQLite backup

For a consistent backup, stop both application processes before copying the database. The helper script does this automatically:

    .\ops\backup.ps1 -VolumeName simpleipaas_ipaas-data -BackupDirectory .\backups

Use docker volume ls to confirm the actual volume name if Docker Compose prefixed it with the project name. Keep backups outside the application volume and encrypt them at rest. Test restoring a backup regularly.

## Tenancy-removal upgrade

Fusetor is single-deployment: one deployment serves one customer. Upgrading a database created before the tenancy removal runs `DatabaseSchemaInitializer.RemoveTenancy` at startup. It drops the `TenantId` columns and their indexes and is safe to run from both hosts at the same time.

- **SQLite:** a `VACUUM INTO` backup named `ipaas.db.bak-pre-remove-tenancy-<yyyyMMdd-HHmmss>` is written beside the database before anything changes. If the backup cannot be created, the migration is skipped and the error is logged; the new version will not start cleanly until the schema is migrated.
- **SQL Server:** no automatic backup is taken. Take a full database backup before starting the new version.
- **Safety check:** if any table contains more than one distinct `TenantId`, the migration refuses to run and logs an error naming the table. It will not merge tenant data. Resolve the data first, then restart.

## Elasticsearch cross-reference ids after the tenancy removal

Cross-reference documents written before the tenancy removal used a document id derived from tenant + list + key. The current code derives the id from list + key, and the dedup check is a multi-get by that id, so old documents are invisible and already-seen keys would be counted as new.

Elasticsearch storage has not shipped. Before enabling it on an upgraded deployment, start with an empty cross-reference index (or delete the old `fusetor-cross-references-*` indices and the `fusetor-cross-references-write` alias). If the storage prefix was changed from the default, use `<prefix>-cross-references-*` instead. Do not delete the log or packet-log indices for this reason; their document ids did not change.

## SQLite restore

Restore only during a planned maintenance window:

    .\ops\restore.ps1 -VolumeName simpleipaas_ipaas-data -BackupFile .\backups\ipaas-20260901-120000.db

The script stops API and Engine, replaces ipaas.db, removes stale WAL/shared-memory files, and starts both processes again. After restore:

1. Check /health.
2. Check /health/ready.
3. Open the Debug Logs page and confirm the Engine heartbeat and startup entries.
4. Run a known-safe manual flow.
5. Confirm queued, successful, and failed execution counts in /metrics.

## Recovery expectations

SQLite with the shared Docker volume is a single-node deployment. Backups protect against data loss, but they do not provide automatic failover. For higher availability, move persistence to a supported client/server database and run API/Engine instances with coordinated storage and backup policy.