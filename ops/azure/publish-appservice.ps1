param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactRoot = Join-Path $repoRoot "artifacts/appservice/$stamp"
$siteRoot = Join-Path $artifactRoot 'site'
$engineRoot = Join-Path $artifactRoot 'engine-publish'
$clientRoot = Join-Path $artifactRoot 'client-publish'
$zipPath = Join-Path $artifactRoot 'fintegrator-appservice.zip'

New-Item -ItemType Directory -Path $siteRoot, $engineRoot, $clientRoot -Force | Out-Null

Push-Location $repoRoot
try {
    dotnet publish src/SimpleIPaaS.Api/SimpleIPaaS.Api.csproj -c $Configuration -o $siteRoot --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }

    dotnet publish src/SimpleIPaaS.Engine/SimpleIPaaS.Engine.csproj -c $Configuration -o $engineRoot --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Engine publish failed.' }

    dotnet publish src/SimpleIPaaS.Client/SimpleIPaaS.Client.csproj -c $Configuration -o $clientRoot --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Client publish failed.' }
}
finally {
    Pop-Location
}

Copy-Item -LiteralPath (Join-Path $clientRoot 'wwwroot') -Destination (Join-Path $siteRoot 'wwwroot') -Recurse

# The separately hosted Blazor client does not have ASP.NET Core's static asset
# alias endpoint. Point its classic script tag at the fingerprinted file.
$siteWebRoot = Join-Path $siteRoot 'wwwroot'
$startupScripts = @(Get-ChildItem -LiteralPath (Join-Path $siteWebRoot '_framework') -Filter 'blazor.webassembly.*.js' -File)
if ($startupScripts.Count -ne 1) { throw "Expected one fingerprinted Blazor startup script, found $($startupScripts.Count)." }
$indexPath = Join-Path $siteWebRoot 'index.html'
$indexHtml = [System.IO.File]::ReadAllText($indexPath)
$plainScript = 'src="_framework/blazor.webassembly.js"'
if (-not $indexHtml.Contains($plainScript)) { throw 'Blazor startup script reference was not found in the published index.html.' }
$indexHtml = $indexHtml.Replace($plainScript, ('src="_framework/' + $startupScripts[0].Name + '"'))
[System.IO.File]::WriteAllText($indexPath, $indexHtml)
foreach ($compressedIndex in @((Join-Path $siteWebRoot 'index.html.br'), (Join-Path $siteWebRoot 'index.html.gz'))) {
    if (Test-Path -LiteralPath $compressedIndex) { Remove-Item -LiteralPath $compressedIndex }
}
foreach ($developmentConfig in @((Join-Path $siteRoot 'appsettings.Development.json'), (Join-Path $siteWebRoot 'appsettings.Development.json'),
    (Join-Path $siteWebRoot 'appsettings.Development.json.br'),
    (Join-Path $siteWebRoot 'appsettings.Development.json.gz'))) {
    if (Test-Path -LiteralPath $developmentConfig) { Remove-Item -LiteralPath $developmentConfig }
}
$jobRoot = Join-Path $siteRoot 'App_Data/jobs/continuous/engine'
New-Item -ItemType Directory -Path $jobRoot -Force | Out-Null
Copy-Item -Path (Join-Path $engineRoot '*') -Destination $jobRoot -Recurse
$runCommand = "@echo off" + [Environment]::NewLine +
    'cd /d "%~dp0"' + [Environment]::NewLine +
    'dotnet SimpleIPaaS.Engine.dll' + [Environment]::NewLine
[System.IO.File]::WriteAllText((Join-Path $jobRoot 'run.cmd'), $runCommand, [System.Text.Encoding]::ASCII)

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($siteRoot, $zipPath)
Write-Output $zipPath

# The zip is the deployable artifact; the expanded publish folders are staging only.
Remove-Item -LiteralPath $siteRoot, $engineRoot, $clientRoot -Recurse -Force -ErrorAction SilentlyContinue

# Keep the three newest timestamped deployments; never touch live-logs or other folders.
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts/appservice') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d{8}-\d{6}$' } |
    Sort-Object Name -Descending |
    Select-Object -Skip 3 |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
