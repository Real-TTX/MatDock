#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Copies the newest MatUi toolkit (the UI of all Mat apps) from a running MatDesign into this app.
.DESCRIPTION
    MatDesign serves the toolkit as files (/ui/matui.css, matui.js, matui.boot.js, matui-icons.svg). MatDock carries its own copy
    in src/MatDock.Web/wwwroot/matui (rule CODE-03: copy, do not link), so it works offline and as an installed app. Run this when MatDesign
    has a new release, then rebuild and look at the pages. The version is the first line of matui.css.
.PARAMETER Url
    Address of MatDesign (default http://localhost:8060, its dev stack).
.EXAMPLE
    ./scripts/update-matui.ps1
.EXAMPLE
    ./scripts/update-matui.ps1 -Url https://design.example.com
#>
param(
    [string]$Url = 'http://localhost:8060'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'src/MatDock.Web/wwwroot/matui'
New-Item -ItemType Directory -Path $target -Force | Out-Null

foreach ($file in 'matui.css', 'matui.js', 'matui.boot.js', 'matui-icons.svg') {
    Invoke-WebRequest -Uri ($Url.TrimEnd('/') + '/ui/' + $file) -OutFile (Join-Path $target $file) -UseBasicParsing
}

Write-Host ('Toolkit updated: ' + (Get-Content (Join-Path $target 'matui.css') -TotalCount 1)) -ForegroundColor Green
