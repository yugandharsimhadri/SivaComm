<#
.SYNOPSIS
    Installs or upgrades SivaComm on the server from an unzipped release.

.DESCRIPTION
    Runs ON THE SERVER (the Azure VM), from the folder the API zip was
    unzipped into. Needs the ASP.NET Core 10 runtime and PostgreSQL; does
    not need the .NET SDK, Node or the source code.

    Every setting comes from appsettings.Production.json in the install
    folder; this script never asks for, or writes, a secret. In order:

      1. checks the config file exists (first run: copies the template there
         and stops, so it can be filled in) and has no REPLACE-ME left
      2. stops the service and copies the new build over the old one,
         keeping the config file (logs and keys live outside the folder)
      3. applies database migrations (SivaComm.Api.exe --migrate)
      4. creates or updates the Windows service: automatic start, restart on
         failure, start after PostgreSQL, ASPNETCORE_ENVIRONMENT=Production
      5. starts it and waits for /health/ready

    Elevated PowerShell, in the unzipped folder:

        cd C:\SivaComm\release
        .\Install-SivaComm.ps1
#>

[CmdletBinding()]
param(
    [string] $InstallDir = 'C:\SivaComm\api',
    [string] $ServiceName = 'SivaCommApi',

    # The published build to install. Default: the api folder beside this script.
    [string] $Source
)

$ErrorActionPreference = 'Stop'
function Say($m)  { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "!!  $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "XX  $m" -ForegroundColor Red; exit 1 }

if (-not $Source) { $Source = Join-Path $PSScriptRoot 'api' }
$Template = Join-Path $PSScriptRoot 'appsettings.Production.json.template'
$Config = Join-Path $InstallDir 'appsettings.Production.json'
$Exe = Join-Path $InstallDir 'SivaComm.Api.exe'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Die 'Run this from an elevated (Administrator) PowerShell: it installs a Windows service.'
}
if (-not (Test-Path (Join-Path $Source 'SivaComm.Api.exe'))) { Die "No published build at $Source." }

$runtimes = & dotnet --list-runtimes 2>$null
if (-not ($runtimes -match 'Microsoft\.AspNetCore\.App 10\.')) {
    Die 'The ASP.NET Core 10 runtime is not installed. Install it first: winget install Microsoft.DotNet.AspNetCore.10'
}

# ------------------------------------------------------------------ 1. config

New-Item -ItemType Directory -Force $InstallDir | Out-Null

if (-not (Test-Path $Config)) {
    Copy-Item $Template $Config
    Warn 'No configuration yet. A template was copied to:'
    Warn "    $Config"
    Warn 'Fill in every REPLACE-ME (DEPLOYMENT_AZURE.md step 5), then run this script again.'
    exit 2
}

$raw = Get-Content $Config -Raw
try { $settings = $raw | ConvertFrom-Json } catch { Die "$Config is not valid JSON: $($_.Exception.Message)" }

# Values only ("Key": "REPLACE-ME...") - the explanatory comments mention it too.
$placeholders = ($raw | Select-String ':\s*"REPLACE-ME' -AllMatches).Matches.Count
if ($placeholders -gt 0) { Die "$Config still contains $placeholders REPLACE-ME value(s). Fill them in first." }

foreach ($dir in @($settings.DataProtection.KeysPath, (Split-Path $settings.Logging.File.PathFormat))) {
    if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
}

# -------------------------------------------------------------------- 2. copy

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Say "Stopping $ServiceName"
    Stop-Service $ServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

Say "Copying the build to $InstallDir (keeping appsettings.Production.json)"
# robocopy without /MIR: never deletes anything in the install folder.
& robocopy $Source $InstallDir /E /NFL /NDL /NJH /NJS /NP /XF appsettings.Production.json appsettings.Local.json | Out-Null
if ($LASTEXITCODE -ge 8) { Die "robocopy failed ($LASTEXITCODE)." }

# ----------------------------------------------------------------- 3. migrate

Say 'Applying database migrations'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
Push-Location $InstallDir
try {
    & $Exe --migrate | Out-Host
    if ($LASTEXITCODE -ne 0) { Die 'Migration failed. The service is stopped; fix the cause (usually Database settings) and re-run.' }
} finally { Pop-Location }

# ----------------------------------------------------------------- 4. service

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

# ------------------------------------------------------------------- 5. start

Say "Starting $ServiceName"
Start-Service $ServiceName

$base = ($settings.Urls -split ';')[0].TrimEnd('/')
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep 2
    try {
        if ((Invoke-WebRequest "$base/health/ready" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) {
            Say "Ready: $base/health/ready"
            exit 0
        }
    } catch { }
}

Die "Started, but $base/health/ready did not answer within a minute. Read the newest log in $(Split-Path $settings.Logging.File.PathFormat)."
