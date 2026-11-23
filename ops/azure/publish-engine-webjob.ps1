param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactRoot = Join-Path $repoRoot "artifacts/appservice/$stamp"
$engineRoot = Join-Path $artifactRoot 'engine-publish'
$siteRoot = Join-Path $artifactRoot 'engine-host'
$jobRoot = Join-Path $siteRoot 'App_Data/jobs/continuous/engine'
$zipPath = Join-Path $artifactRoot 'fintegrator-engine-webjob.zip'

New-Item -ItemType Directory -Path $engineRoot, $jobRoot -Force | Out-Null

Push-Location $repoRoot
try {
    dotnet publish src/SimpleIPaaS.Engine/SimpleIPaaS.Engine.csproj -c $Configuration -o $engineRoot --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Engine publish failed.' }
}
finally {
    Pop-Location
}

foreach ($developmentConfig in @((Join-Path $engineRoot 'appsettings.Development.json'),
    (Join-Path $engineRoot 'appsettings.Development.json.br'),
    (Join-Path $engineRoot 'appsettings.Development.json.gz'))) {
    if (Test-Path -LiteralPath $developmentConfig) { Remove-Item -LiteralPath $developmentConfig }
}
Copy-Item -Path (Join-Path $engineRoot '*') -Destination $jobRoot -Recurse
$runCommand = "@echo off" + [Environment]::NewLine +
    'cd /d "%~dp0"' + [Environment]::NewLine +
    'dotnet SimpleIPaaS.Engine.dll' + [Environment]::NewLine
[System.IO.File]::WriteAllText((Join-Path $jobRoot 'run.cmd'), $runCommand, [System.Text.Encoding]::ASCII)
[System.IO.File]::WriteAllText((Join-Path $jobRoot 'settings.job'), '{"is_singleton":true}', [System.Text.Encoding]::ASCII)

$hostIndex = '<!doctype html><html><head><meta charset="utf-8"><title>Fusetor background worker</title></head><body><main>Fusetor background processing host. No interactive application is served here.</main></body></html>'
[System.IO.File]::WriteAllText((Join-Path $siteRoot 'index.html'), $hostIndex, [System.Text.Encoding]::UTF8)

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($siteRoot, $zipPath)
Write-Output $zipPath

# The zip is the deployable artifact; the expanded publish folders are staging only.
Remove-Item -LiteralPath $engineRoot, $siteRoot -Recurse -Force -ErrorAction SilentlyContinue

Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts/appservice') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d{8}-\d{6}$' } |
    Sort-Object Name -Descending |
    Select-Object -Skip 3 |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
