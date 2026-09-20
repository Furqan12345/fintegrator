# SimpleIPaaS operations runbook

## Health endpoints

- /health/live confirms that the API process is responding. It does not require the database.
- /health checks API database reachability and is suitable for the API container healthcheck.
- /health/ready checks the database and confirms that the standalone Engine has written a recent heartbeat.
- /metrics returns Prometheus text metrics. It requires the normal X-Api-Key header.

The API and Engine must use the same SQLite file. If /health is healthy but /health/ready is unhealthy, inspect the Engine container first.

## Metrics and alerts

Import ops/prometheus/simpleipaas-alerts.yml into the Prometheus alerting rules configuration. Configure Prometheus to scrape the API /metrics endpoint with an API key header. The rules cover:

- Engine heartbeat loss.
- API 5xx responses.
- Failed flow executions.
- A growing queued-execution backlog.

The application does not send execution payloads or errors to an arbitrary alert URL. Route alerts through the organization’s existing Prometheus/Alertmanager policy.

## SQLite backup

For a consistent backup, stop both application processes before copying the database. The helper script does this automatically:

    .\ops\backup.ps1 -VolumeName simpleipaas_ipaas-data -BackupDirectory .\backups

Use docker volume ls to confirm the actual volume name if Docker Compose prefixed it with the project name. Keep backups outside the application volume and encrypt them at rest. Test restoring a backup regularly.

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