<#
.SYNOPSIS
    Builds and installs SivaComm in one go, on a server that has the source,
    the .NET 10 SDK and Node.

.DESCRIPTION
    The usual path is two steps on two machines: New-Release.ps1 on a
    development machine makes the zips, and Install-SivaComm.ps1 on the server
    installs the API zip (DEPLOYMENT_AZURE.md). This script is the shortcut
    for a server that can build: it makes the release folder and installs it.
    The dashboard it builds is not installed here - it goes to Cloudflare
    Pages.

    Elevated PowerShell, in the repository root:

        .\deploy\Deploy-SivaComm.ps1
#>

[CmdletBinding()]
param(
    [string] $InstallDir = 'C:\SivaComm\api',
    [string] $ServiceName = 'SivaCommApi'
)

$ErrorActionPreference = 'Stop'
$staging = Join-Path ([IO.Path]::GetTempPath()) "sivacomm-release-$([Guid]::NewGuid().ToString('N'))"

& (Join-Path $PSScriptRoot 'New-Release.ps1') -Out $staging -NoZip
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $staging 'api-release\Install-SivaComm.ps1') -InstallDir $InstallDir -ServiceName $ServiceName
$code = $LASTEXITCODE
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
exit $code
