<#
  Compiles installer\Glance.iss into dist\Glance-Setup-<version>.exe.
  Run .\build\publish.ps1 first. Needs Inno Setup 6:  winget install JRSoftware.InnoSetup
#>
param([switch]$Sign)   # sign the finished installer (see build\sign.ps1)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path (Join-Path $repo 'dist\Glance\Glance.App.exe')) -or -not (Test-Path (Join-Path $repo 'dist\Glance\Glance.Service.exe'))) {
    throw 'dist\Glance is missing. Run .\build\publish.ps1 first (without -FrameworkDependent for an installer).'
}

$iscc = @(
    "$env:ProgramFiles (x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

# keep the installer's version in step with src\Directory.Build.props
[xml]$props = Get-Content (Join-Path $repo 'src\Directory.Build.props')
$version = $props.Project.PropertyGroup.Version

& $iscc "/DAppVersion=$version" (Join-Path $repo 'installer\Glance.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$setup = Join-Path $repo "dist\Glance-Setup-$version.exe"
if ($Sign) {
    . (Join-Path $PSScriptRoot 'sign.ps1')
    Invoke-GlanceSign -Path $setup
}
Write-Host ("Installer: {0}  ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
