#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the current source and runs a local test instance of MatDock next to whatever else runs on this machine.
.DESCRIPTION
    A separate container (matdock-localtest) with its own data volume, so it never touches the live instance or its data. It mounts the Docker
    socket, so the "local" environment is this machine's Docker: containers, volumes, networks and images of the real Docker are what you see and
    change. Because of that it listens on 127.0.0.1 only (the test account is an administrator, the socket is root on the host).
    Development mode seeds the test account tester / Tester123! and the local environment is created through the API.
.PARAMETER Port
    The port on 127.0.0.1 (default 4470).
.PARAMETER Fresh
    Also delete the test instance's data volume (users, targets, schedules) before starting.
.PARAMETER Stop
    Stop and remove the test instance (and keep its data unless -Fresh is given) instead of starting it.
.EXAMPLE
    ./scripts/localtest.ps1
.EXAMPLE
    ./scripts/localtest.ps1 -Fresh
.EXAMPLE
    ./scripts/localtest.ps1 -Stop -Fresh
#>
param(
    [int]$Port = 4470,
    [switch]$Fresh,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$name = 'matdock-localtest'
$volume = 'matdock-localtest-data'
$image = 'matdock:localtest'
$apiKey = 'localtest-key'
$root = Split-Path -Parent $PSScriptRoot

# Removing a container that is not there is fine; docker prints to stderr then, which must not stop the script.
function Invoke-Quiet([scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try { & $Command *> $null } finally { $ErrorActionPreference = $previous }
}

Invoke-Quiet { docker rm -f $name }
if ($Fresh) {
    Invoke-Quiet { docker volume rm -f $volume }
}

if ($Stop) {
    Write-Host "Test instance stopped$(if ($Fresh) { ' and its data deleted' } else { '; its data is kept' })." -ForegroundColor Green
    return
}

Push-Location $root
try {
    docker build -q -t $image . | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker build failed.' }
}
finally {
    Pop-Location
}

docker run -d --name $name --restart unless-stopped `
    -p "127.0.0.1:${Port}:4455" `
    -e ASPNETCORE_ENVIRONMENT=Development `
    -e MatDock__Api__Key=$apiKey `
    -v "${volume}:/data" `
    -v /var/run/docker.sock:/var/run/docker.sock `
    $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'docker run failed.' }

$url = "http://127.0.0.1:$Port"
$up = $false
for ($i = 0; $i -lt 40 -and -not $up; $i++) {
    try { $up = (Invoke-WebRequest -Uri "$url/Account/Login" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 }
    catch { Start-Sleep -Seconds 1 }
}
if (-not $up) { throw "The test instance did not come up. See: docker logs $name" }

# The local environment (this machine's Docker), so there is something to look at from the first click.
Invoke-RestMethod -Method Put -Uri "$url/api/v1/environments/local" -Headers @{ 'X-Api-Key' = $apiKey } | Out-Null

Write-Host ''
Write-Host "MatDock test instance: $url" -ForegroundColor Green
Write-Host '  sign in:  tester / Tester123!   (Development mode seeds this administrator)'
Write-Host '  it sees the Docker of this machine through the mounted socket, so changes are real'
Write-Host "  stop:     ./scripts/localtest.ps1 -Stop     (add -Fresh to delete its data too)"
