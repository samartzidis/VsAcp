#requires -Version 7
<#
.SYNOPSIS
  Builds the extension and deploys it into the Visual Studio Experimental instance the way F5 does.

.DESCRIPTION
  Runs Visual Studio's own deploy step headless (`devenv VsAcp.slnx /Deploy`): VS builds the project, then
  uninstalls any installed copy of the extension by id, installs the new VSIX into a fresh folder of the
  Exp hive, enables it and marks the extension configuration as changed, so the next Exp start loads it.
  This is what F5 does (its build runs with DeployExtension=false; the deploy is VS's, not MSBuild's),
  and it handles version changes. It neither runs VSIXInstaller nor needs `devenv /updateconfiguration`.
  The `install to VS2026` task checks a real VSIXInstaller install, the way users get it.

.PARAMETER RootSuffix
  Experimental-instance root suffix. Defaults to Exp (the project's VSSDKTargetPlatformRegRootSuffix).

.EXAMPLE
  pwsh -NoProfile -File .vscode\Deploy-VsixExp.ps1
#>
[CmdletBinding()]
param(
    [string] $RootSuffix = 'Exp',
    [string] $Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Stop-Deploy([string] $message) {
    Write-Host ''
    Write-Host "FAILED: $message" -ForegroundColor Red
    exit 1
}

Write-Step 'Preflight'
# A running Exp instance locks the extension's files; the deploy would fail or half-succeed.
$open = @(Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" |
        Where-Object { $_.CommandLine -match "(?i)/rootsuffix[\s:=]+$([regex]::Escape($RootSuffix))(\s|$)" })
if ($open.Count -gt 0) {
    Stop-Deploy "The $RootSuffix instance is running (devenv.exe PID $($open.ProcessId -join ', ')). Close it and run again."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { Stop-Deploy "vswhere.exe not found at $vswhere" }
$products = 'Microsoft.VisualStudio.Product.Enterprise', 'Microsoft.VisualStudio.Product.Professional', 'Microsoft.VisualStudio.Product.Community'
$vs = & $vswhere -version '[18.0,)' -prerelease -products $products -latest -format json | ConvertFrom-Json | Select-Object -First 1
if (-not $vs) { Stop-Deploy 'No Visual Studio 18 (2026) installation found.' }
# devenv.com is the console front end of devenv.exe: it waits for the deploy and streams its output.
$devenv = Join-Path $vs.installationPath 'Common7\IDE\devenv.com'
if (-not (Test-Path $devenv)) { Stop-Deploy "devenv.com not found at $devenv" }
Write-Host "    devenv   $devenv"

$solution = Join-Path $PSScriptRoot '..\VsAcp.slnx'
# Headless devenv cannot restore NuGet packages ("details for project VsAcp could not be loaded") and
# builds with whatever project.assets.json is on disk, so restore first.
Write-Step 'Restoring packages'
& dotnet restore $solution --verbosity quiet
if ($LASTEXITCODE -ne 0) { Stop-Deploy "dotnet restore exited with code $LASTEXITCODE." }

Write-Step "Building and deploying to $RootSuffix"
& $devenv $solution /Deploy $Configuration /Project 'src\VsAcp\VsAcp.csproj'
if ($LASTEXITCODE -ne 0) { Stop-Deploy "devenv /Deploy exited with code $LASTEXITCODE." }

Write-Host ''
Write-Host "Deployed to $RootSuffix." -ForegroundColor Green
exit 0
