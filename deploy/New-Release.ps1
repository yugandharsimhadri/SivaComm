<#
.SYNOPSIS
    Builds the two zips a deployment copies: the API for the server, the
    dashboard for Cloudflare Pages.

.DESCRIPTION
    Runs on a development machine with the .NET 10 SDK and Node. Output in
    artifacts\ (git-ignored):

      SivaComm-api-<date>-<commit>.zip         copy to the Azure VM, unzip, run Install-SivaComm.ps1
        api\                                   the published API (win-x64, framework-dependent)
        Install-SivaComm.ps1
        appsettings.Production.json.template
        cloudflared\config.yml.template
        sql\01-create-database.sql             role, database, schema, privileges
        sql\full-schema.sql                    the above + every table, one psql run
        sql\migrate.sql                        tables only, idempotent
        DEPLOYMENT_AZURE.md
        VERSION.txt

      SivaComm-dashboard-<date>-<commit>.zip   upload to Cloudflare Pages (sivacom.sivayaantechnologies.com)
        index.html, assets\, _headers          built with VITE_API_URL from dashboard\.env.production

.PARAMETER Out
    Folder to assemble into. Default artifacts\release.

.PARAMETER NoZip
    Leave the folders unzipped (Deploy-SivaComm.ps1 uses this).

.EXAMPLE
    .\deploy\New-Release.ps1
#>

[CmdletBinding()]
param(
    [string] $Out,
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
function Say($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Die($m) { Write-Host "XX  $m" -ForegroundColor Red; exit 1 }

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ApiProj = Join-Path $Repo 'backend\src\SivaComm.Api\SivaComm.Api.csproj'
$Dashboard = Join-Path $Repo 'dashboard'
$Artifacts = Join-Path $Repo 'artifacts'
if (-not $Out) { $Out = Join-Path $Artifacts 'release' }

$env:MSBUILDDISABLENODEREUSE = '1'

$commit = (& git -C $Repo rev-parse --short HEAD 2>$null)
if (-not $commit) { $commit = 'nogit' }
$dirty = (& git -C $Repo status --porcelain 2>$null)
if ($dirty) { $commit = "$commit-dirty" }
$stamp = Get-Date -Format 'yyyyMMdd'

$ApiOut = Join-Path $Out 'api-release'
$DashOut = Join-Path $Out 'dashboard'
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force $ApiOut, $DashOut | Out-Null

# ---------------------------------------------------------------------- API

Say 'Publishing the API (Release, win-x64, framework-dependent)'
& dotnet publish $ApiProj -c Release -r win-x64 --self-contained false -o (Join-Path $ApiOut 'api') --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { Die 'dotnet publish failed.' }

# The dashboard is on Cloudflare Pages; the API does not serve it.
Remove-Item (Join-Path $ApiOut 'api\wwwroot') -Recurse -Force -ErrorAction SilentlyContinue
# No web.config: SivaComm runs as a Windows service, never under IIS, and a
# web.config in the folder is an invitation for IIS to claim it (and port 6071).
Remove-Item (Join-Path $ApiOut 'api\web.config') -Force -ErrorAction SilentlyContinue
# Development-only files have no business on a server.
Remove-Item (Join-Path $ApiOut 'api\appsettings.Development.json'), (Join-Path $ApiOut 'api\appsettings.Local.json') -Force -ErrorAction SilentlyContinue

Copy-Item (Join-Path $PSScriptRoot 'Install-SivaComm.ps1') $ApiOut
Copy-Item (Join-Path $PSScriptRoot 'appsettings.Production.json.template') $ApiOut
New-Item -ItemType Directory -Force (Join-Path $ApiOut 'cloudflared') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'cloudflared\config.yml.template') (Join-Path $ApiOut 'cloudflared')
Copy-Item (Join-Path $Repo 'docs\DEPLOYMENT_AZURE.md') $ApiOut

# Database scripts, generated from this build's own migrations so the zip can
# never carry SQL that disagrees with its program.
Say 'Generating the database scripts'
$SqlOut = Join-Path $ApiOut 'sql'
New-Item -ItemType Directory -Force $SqlOut | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'sql\01-create-database.sql') $SqlOut
& (Join-Path $PSScriptRoot 'New-SchemaScripts.ps1') -OutDir $SqlOut | Out-Host
"SivaComm API`ncommit $commit`nbuilt $(Get-Date -Format 'yyyy-MM-dd HH:mm')" | Set-Content (Join-Path $ApiOut 'VERSION.txt') -Encoding ascii

# ---------------------------------------------------------------- dashboard

Say 'Building the dashboard (VITE_API_URL from dashboard\.env.production)'
Push-Location $Dashboard
try {
    & npm ci --no-audit --no-fund | Out-Host
    if ($LASTEXITCODE -ne 0) { Die 'npm ci failed.' }
    & npm run build | Out-Host
    if ($LASTEXITCODE -ne 0) { Die 'Dashboard build failed.' }
} finally { Pop-Location }

Copy-Item (Join-Path $Dashboard 'dist\*') $DashOut -Recurse
$apiUrl = (Select-String -Path (Join-Path $Dashboard '.env.production') -Pattern '^VITE_API_URL=(.+)$').Matches[0].Groups[1].Value
if (-not (Select-String -Path (Join-Path $DashOut 'assets\*.js') -SimpleMatch $apiUrl -Quiet)) {
    Die "The dashboard bundle does not contain $apiUrl - VITE_API_URL was not applied."
}

# --------------------------------------------------------------------- zips

if ($NoZip) { Say "Folders ready in $Out"; exit 0 }

# Not Compress-Archive: Windows PowerShell 5.1's version writes entry names
# with backslashes ("assets\index.js"), which Cloudflare Pages and every
# non-Windows unzip treat as one file with a backslash in its name.
function New-Zip([string] $dir, [string] $zip) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $root = (Resolve-Path $dir).Path.TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem $dir -Recurse -File | ForEach-Object {
            $name = $_.FullName.Substring($root.Length).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
}

$apiZip = Join-Path $Artifacts "SivaComm-api-$stamp-$commit.zip"
$dashZip = Join-Path $Artifacts "SivaComm-dashboard-$stamp-$commit.zip"
Remove-Item $apiZip, $dashZip -ErrorAction SilentlyContinue
New-Zip $ApiOut $apiZip
New-Zip $DashOut $dashZip

Say "API zip       : $apiZip ($([math]::Round((Get-Item $apiZip).Length / 1MB, 1)) MB)"
Say "Dashboard zip : $dashZip ($([math]::Round((Get-Item $dashZip).Length / 1KB)) KB)"
Say "Dashboard calls: $apiUrl"
if ($dirty) { Write-Host '!!  Built from uncommitted changes (the name says -dirty).' -ForegroundColor Yellow }
