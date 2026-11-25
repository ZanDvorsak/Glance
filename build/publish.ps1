<#
  Builds a Glance package.

  Usage (from anywhere):
      .\build\publish.ps1                      # self-contained: no .NET needed on the target PC (~200 MB folder, ~90 MB zip)
      .\build\publish.ps1 -FrameworkDependent  # small (~30 MB), needs the .NET 8 Desktop Runtime installed
      .\build\publish.ps1 -NoZip               # just the folder

  The editor and the runner are published into the SAME folder, so the shared files exist only once.
  The editor finds the runner (and the runner finds the editor) next to itself, and the "start at login"
  task points at the runner in this folder, so keep it somewhere stable (an installer puts it in Program Files).
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'src'
$dist = Join-Path $repo 'dist'
$flavor = if ($FrameworkDependent) { 'fxdep' } else { 'selfcontained' }
$out = Join-Path $dist 'Glance'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (-not $dotnet) { throw '.NET 8 SDK not found. Install it with: winget install Microsoft.DotNet.SDK.8' }

[xml]$props = Get-Content (Join-Path $src 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
Write-Host "Glance $version ($Runtime, $flavor)" -ForegroundColor Cyan

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
foreach ($project in 'Glance.App', 'Glance.Runner') {
    Write-Host "Publishing $project ..." -ForegroundColor Cyan
    $args = @('publish', (Join-Path $src "$project\$project.csproj"), '-c', $Configuration, '-r', $Runtime,
              '--self-contained', $selfContained, '-p:DebugType=None', '-p:DebugSymbols=false', '-p:SkipHost=true', '-o', $out)
    if (-not $FrameworkDependent) { $args += '-p:PublishReadyToRun=true' }
    & $dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

# native libraries ship large debug symbols that users never need
Get-ChildItem $out -Recurse -Filter *.pdb | Remove-Item -Force

Copy-Item (Join-Path $repo 'README.md') $out -Force
Copy-Item (Join-Path $repo 'assets\glance.ico') $out -Force

$bytes = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("Package folder: {0}  ({1:N0} MB, {2} files)" -f $out, ($bytes / 1MB), (Get-ChildItem $out -Recurse -File).Count) -ForegroundColor Green

if (-not $NoZip) {
    $suffix = if ($FrameworkDependent) { "$Runtime-fx-dependent" } else { $Runtime }
    $zip = Join-Path $dist "Glance-$version-$suffix.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # antivirus often holds freshly written DLLs for a moment, so retry
    for ($try = 1; $try -le 6; $try++) {
        try {
            [IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
            break
        }
        catch {
            if ($try -eq 6) { throw }
            if (Test-Path $zip) { Remove-Item $zip -Force }
            Write-Host "  zip attempt $try failed ($($_.Exception.InnerException.Message)); retrying..." -ForegroundColor Yellow
            Start-Sleep -Seconds 3
        }
    }
    Write-Host ("Zip: {0}  ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
}
