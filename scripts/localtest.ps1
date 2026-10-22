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
.PARAMETER MatSmtp
    Name of a running MatSMTP container: the test instance sends its mail through it (the MATSMTP_* variables Mattiverse would write). A relay user
    "matdock-localtest" is made through MatSMTP's management API (its key is read from that container, never printed) and the test instance joins
    its Docker network. -Stop with -MatSmtp removes the relay user again. Mind where that MatSMTP delivers: mail leaves through its account.
.EXAMPLE
    ./scripts/localtest.ps1
.EXAMPLE
    ./scripts/localtest.ps1 -Fresh
.EXAMPLE
    ./scripts/localtest.ps1 -MatSmtp mvtest-matsmtp-matsmtp
.EXAMPLE
    ./scripts/localtest.ps1 -Stop -Fresh
#>
param(
    [int]$Port = 4470,
    [switch]$Fresh,
    [switch]$Stop,
    [string]$MatSmtp
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

$relayUser = 'matdock-localtest'

# MatSMTP's management API from a running MatSMTP container: its URL on this machine and the key from the container's own environment.
function Get-MatSmtpApi([string]$Container) {
    $vars = docker inspect $Container --format '{{range .Config.Env}}{{println .}}{{end}}'
    if ($LASTEXITCODE -ne 0) { throw "No container named $Container." }
    $key = ($vars | Where-Object { $_ -match 'API_?KEY=' } | Select-Object -First 1) -replace '^[^=]*=', ''
    if (-not $key) { throw "$Container has no API key in its environment." }
    $port = (docker port $Container 2525/tcp | Select-Object -First 1) -replace '^.*:', ''
    if (-not $port) { throw "$Container publishes no web port." }
    [pscustomobject]@{ Url = "http://127.0.0.1:$port/api/v1"; Headers = @{ 'X-Api-Key' = $key } }
}

Invoke-Quiet { docker rm -f $name }
if ($Fresh) {
    Invoke-Quiet { docker volume rm -f $volume }
}

if ($Stop) {
    if ($MatSmtp) {
        $api = Get-MatSmtpApi $MatSmtp
        try { Invoke-RestMethod -Method Delete -Uri "$($api.Url)/relay-users/${relayUser}?managedBy=$relayUser" -Headers $api.Headers | Out-Null }
        catch { if ($_.Exception.Response.StatusCode -ne 404) { throw } }
        Write-Host "Relay user $relayUser removed from $MatSmtp." -ForegroundColor Green
    }
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

$runArgs = @(
    'run', '-d', '--name', $name, '--restart', 'unless-stopped',
    '-p', "127.0.0.1:${Port}:4455",
    '-e', 'ASPNETCORE_ENVIRONMENT=Development',
    '-e', "MatDock__Api__Key=$apiKey",
    '-v', "${volume}:/data",
    '-v', '/var/run/docker.sock:/var/run/docker.sock')

if ($MatSmtp) {
    $api = Get-MatSmtpApi $MatSmtp
    $status = Invoke-RestMethod -Uri "$($api.Url)/status" -Headers $api.Headers
    # The password is only returned when the user is made or renewed, so every start renews it.
    $body = @{ displayName = 'MatDock (local test)'; allowedSenders = @('matdock-localtest@localhost'); managedBy = $relayUser; renewSecret = $true } | ConvertTo-Json
    $relay = Invoke-RestMethod -Method Put -Uri "$($api.Url)/relay-users/$relayUser" -Headers $api.Headers -ContentType 'application/json' -Body $body
    $networks = @(docker inspect $MatSmtp --format '{{range $k, $v := .NetworkSettings.Networks}}{{println $k}}{{end}}' | Where-Object { $_ })
    $network = $networks | Where-Object { $_ -notlike '*_default' } | Select-Object -First 1
    if (-not $network) { $network = $networks[0] }
    $runArgs += @('--network', $network,
        '-e', "MATSMTP_HOST=$MatSmtp", '-e', "MATSMTP_PORT=$($status.smtpPort)", '-e', "MATSMTP_TLS=$($status.smtpTls)",
        '-e', "MATSMTP_USERNAME=$($relay.username)", '-e', "MATSMTP_PASSWORD=$($relay.password)", '-e', 'MATSMTP_FROM=matdock-localtest@localhost')
}

docker @runArgs $image | Out-Null
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
if ($MatSmtp) { Write-Host "  mail:     through $MatSmtp (relay user $relayUser); Settings > Notifications shows it as fixed" }
Write-Host '  it sees the Docker of this machine through the mounted socket, so changes are real'
Write-Host "  stop:     ./scripts/localtest.ps1 -Stop     (add -Fresh to delete its data too)"
