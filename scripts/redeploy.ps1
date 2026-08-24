#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Rebuilds the MatDock container and (re)deploys the stack.
.DESCRIPTION
    This is the project's live-reload/test workflow: rebuild the image and redeploy.
.PARAMETER Dev
    Also start the dev stack (adds the SQLite web viewer).
.EXAMPLE
    ./scripts/redeploy.ps1
.EXAMPLE
    ./scripts/redeploy.ps1 -Dev
#>
param(
    [switch]$Dev
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if ($Dev) {
        docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
    }
    else {
        docker compose up -d --build
    }

    Write-Host ''
    Write-Host 'MatDock läuft auf http://localhost:4455' -ForegroundColor Green
    if ($Dev) {
        Write-Host 'SQLite-Web läuft auf http://localhost:8085' -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
