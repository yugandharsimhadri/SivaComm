<#
.SYNOPSIS
    Builds SivaComm and installs or upgrades it as a Windows service.

.DESCRIPTION
    Every setting comes from appsettings.Production.json in the install
    folder; this script never asks for, or writes, a secret. In order:

      1. checks the config file exists (first run: copies the template there
         and stops, so it can be filled in)
      2. builds the dashboard (npm) and publishes the API (dotnet)
      3. stops the service, copies the new build over the old one - keeping
         the config file, logs and keys folders
      4. applies database migrations (SivaComm.Api.exe --migrate)
      5. creates or updates the service: automatic start, restart on
         failure, start after PostgreSQL, ASPNETCORE_ENVIRONMENT=Production
      6. starts it and waits for /health/ready

    Run from an elevated PowerShell in the repository root:

        .\deploy\Deploy-SivaComm.ps1
        .\deploy\Deploy-SivaComm.ps1 -InstallDir D:\SivaComm -ServiceName SivaCommApi
#>

[CmdletBinding()]
param(
    [string] $InstallDir = 'C:\Services\SivaComm',
    [string] $ServiceName = 'SivaCommApi',
    [switch] $SkipDashboard
)

$ErrorActionPreference = 'Stop'
function Say($m)  { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "!!  $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "XX  $m" -ForegroundColor Red; exit 1 }

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ApiProj = Join-Path $Repo 'backend\src\SivaComm.Api\SivaComm.Api.csproj'
$Dashboard = Join-Path $Repo 'dashboard'
$Template = Join-Path $PSScriptRoot 'appsettings.Production.json.template'
$Config = Join-Path $InstallDir 'appsettings.Production.json'
$Exe = Join-Path $InstallDir 'SivaComm.Api.exe'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Die 'Run this from an elevated (Administrator) PowerShell: it installs a Windows service.'
}

# ------------------------------------------------------------------ 1. config

New-Item -ItemType Directory -Force $InstallDir | Out-Null

if (-not (Test-Path $Config)) {
    Copy-Item $Template $Config
    Warn "No configuration yet. A template was copied to:"
    Warn "    $Config"
    Warn "Fill in every REPLACE-ME (docs/DEPLOYMENT.md step 3), then run this script again."
    exit 2
}

$settings = Get-Content $Config -Raw | ConvertFrom-Json
# Values only ("Key": "REPLACE-ME…") - the explanatory comments mention it too.
$placeholders = (Get-Content $Config -Raw | Select-String ':\s*"REPLACE-ME' -AllMatches).Matches.Count
if ($placeholders -gt 0) { Die "$Config still contains $placeholders REPLACE-ME value(s). Fill them in first." }

foreach ($dir in @($settings.DataProtection.KeysPath, (Split-Path $settings.Logging.File.PathFormat))) {
    if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
}

# ------------------------------------------------------------------- 2. build

if (-not $SkipDashboard) {
    Say 'Building the dashboard'
    Push-Location $Dashboard
    try {
        & npm ci --no-audit --no-fund | Out-Host
        if ($LASTEXITCODE -ne 0) { Die 'npm ci failed.' }
        & npm run build | Out-Host
        if ($LASTEXITCODE -ne 0) { Die 'Dashboard build failed.' }
    } finally { Pop-Location }
}

$Staging = Join-Path ([IO.Path]::GetTempPath()) "sivacomm-publish-$([Guid]::NewGuid().ToString('N'))"
Say "Publishing the API to $Staging"
& dotnet publish $ApiProj -c Release -o $Staging --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { Die 'dotnet publish failed.' }

# -------------------------------------------------------------------- 3. copy

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Say "Stopping $ServiceName"
    Stop-Service $ServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

Say "Copying the build to $InstallDir (keeping appsettings.Production.json, logs, keys)"
# robocopy without /MIR: never deletes anything in the install folder.
& robocopy $Staging $InstallDir /E /NFL /NDL /NJH /NJS /NP /XF appsettings.Production.json appsettings.Local.json | Out-Null
if ($LASTEXITCODE -ge 8) { Die "robocopy failed ($LASTEXITCODE)." }
Remove-Item $Staging -Recurse -Force

# ----------------------------------------------------------------- 4. migrate

Say 'Applying database migrations'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
Push-Location $InstallDir
try {
    & $Exe --migrate | Out-Host
    if ($LASTEXITCODE -ne 0) { Die 'Migration failed. The previous build is still installed but stopped; fix and re-run.' }
} finally { Pop-Location }

# ----------------------------------------------------------------- 5. service

if (-not $existing) {
    Say "Creating service $ServiceName"
    & sc.exe create $ServiceName binPath= "`"$Exe`"" start= auto DisplayName= "SivaComm Messaging API" | Out-Null
    if ($LASTEXITCODE -ne 0) { Die 'sc.exe create failed.' }
} else {
    & sc.exe config $ServiceName binPath= "`"$Exe`"" start= auto | Out-Null
}

& sc.exe description $ServiceName "Sivayaan Communications: WhatsApp OTP and messaging for Sivayaan applications." | Out-Null

# Start after a local PostgreSQL, so a reboot does not race it.
$pg = Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pg -and $settings.Database.Host -in @('localhost', '127.0.0.1', '::1')) {
    & sc.exe config $ServiceName depend= $pg.Name | Out-Null
}

$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
Set-ItemProperty -Path $regPath -Name Environment -Value @('ASPNETCORE_ENVIRONMENT=Production') -Type MultiString

# Restart after 5 s, 15 s, 60 s; reset the count after a day.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

# ------------------------------------------------------------------- 6. start

Say "Starting $ServiceName"
Start-Service $ServiceName

$url = ($settings.Urls -split ';')[0].TrimEnd('/') + '/health/ready'
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep 2
    try {
        if ((Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) {
            Say "Ready: $url"
            Say "Dashboard: $(($settings.Urls -split ';')[0])/"
            exit 0
        }
    } catch { }
}

Die "Started, but $url did not answer ready within a minute. Read the log in $(Split-Path $settings.Logging.File.PathFormat)."
