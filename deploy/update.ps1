# Pulls the platform branch from GitHub and redeploys only when it changed. Safe to run from Task Scheduler.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
git fetch --quiet origin platform
if ($LASTEXITCODE -ne 0) { throw 'git fetch failed' }
if (((git rev-parse HEAD) -eq (git rev-parse origin/platform)) -and -not $Force) { exit 0 }
git merge --ff-only --quiet origin/platform
if ($LASTEXITCODE -ne 0) { throw 'local changes prevent update' }
Set-Location (Join-Path $PSScriptRoot '.')
docker compose up -d --build --remove-orphans
if ($LASTEXITCODE -ne 0) { throw 'docker compose failed' }
Write-Output "$(Get-Date -Format o) deployed $(git rev-parse --short HEAD)"
