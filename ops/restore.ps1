param(
    [Parameter(Mandatory = $true)]
    [string]$BackupFile,
    [string]$VolumeName = "simpleipaas_ipaas-data"
)

$ErrorActionPreference = "Stop"
$backupPath = (Resolve-Path -LiteralPath $BackupFile).Path
$backupRoot = Split-Path -Parent $backupPath
$fileName = Split-Path -Leaf $backupPath

Write-Host "Stopping API and Engine before restore..."
docker compose stop engine api
try {
    docker run --rm -v ("{0}:/data" -f $VolumeName) -v ("{0}:/backup:ro" -f $backupRoot) busybox sh -c "cp /backup/$fileName /data/ipaas.db && rm -f /data/ipaas.db-wal /data/ipaas.db-shm"
    if ($LASTEXITCODE -ne 0) {
        throw "Docker restore failed."
    }

    Write-Host "Restore completed from $backupPath"
}
finally {
    Write-Host "Starting API and Engine..."
    docker compose start api engine
}