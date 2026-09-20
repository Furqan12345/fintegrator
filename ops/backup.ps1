param(
    [string]$VolumeName = "simpleipaas_ipaas-data",
    [string]$BackupDirectory = ".\backups"
)

$ErrorActionPreference = "Stop"
$backupRoot = (New-Item -ItemType Directory -Force -Path $BackupDirectory).FullName
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$fileName = "ipaas-$stamp.db"

Write-Host "Stopping API and Engine for a consistent SQLite backup..."
docker compose stop engine api
try {
    docker run --rm -v ("{0}:/data:ro" -f $VolumeName) -v ("{0}:/backup" -f $backupRoot) busybox sh -c "cp /data/ipaas.db /backup/$fileName"
    if ($LASTEXITCODE -ne 0) {
        throw "Docker backup failed."
    }

    Write-Host "Backup written to $([IO.Path]::Combine($backupRoot, $fileName))"
}
finally {
    Write-Host "Starting API and Engine..."
    docker compose start api engine
}