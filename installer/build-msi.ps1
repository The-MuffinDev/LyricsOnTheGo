# Builds the LyricsOnTheGo MSI end-to-end:
#   1) publishes a self-contained build (target PC needs no .NET runtime),
#   2) ensures the branded wizard images exist,
#   3) compiles installer\LyricsOnTheGo.wxs into dist\LyricsOnTheGo-<version>.msi with WiX v5.
#
# The version is read from the single source of truth — <Version> in LyricsOnTheGo.csproj — so
# nothing here needs editing on a version bump.
#
# One-time prerequisites (install manually):
#   dotnet tool install --global wix
#   wix extension add -g WixToolset.UI.wixext
#
# Then, from the repo root:
#   powershell -File installer\build-msi.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Single source of truth: the <Version> element in the project file.
$csprojPath = Join-Path $root 'src\LyricsOnTheGo\LyricsOnTheGo.csproj'
$version = @(([xml](Get-Content $csprojPath)).Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw "Could not read <Version> from $csprojPath" }
Write-Host "== Version $version (from LyricsOnTheGo.csproj) ==" -ForegroundColor Cyan

Write-Host '== Stopping any running instance ==' -ForegroundColor Cyan
Get-Process LyricsOnTheGo -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host '== Publishing (self-contained, win-x64) ==' -ForegroundColor Cyan
dotnet publish src\LyricsOnTheGo\LyricsOnTheGo.csproj -c Release -r win-x64 --self-contained true `
    -o publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host '== Ensuring wizard images exist ==' -ForegroundColor Cyan
$haveImages = (Test-Path (Join-Path $PSScriptRoot 'wix-banner.bmp')) -and `
              (Test-Path (Join-Path $PSScriptRoot 'wix-dialog.bmp'))
if (-not $haveImages) {
    try { & (Join-Path $PSScriptRoot 'make-installer-images.ps1') }
    catch { Write-Warning "Could not regenerate wizard images: $_. Using whatever exists." }
}

Write-Host '== Checking WiX ==' -ForegroundColor Cyan
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Warning 'WiX not found. Install it once with:'
    Write-Host    '    dotnet tool install --global wix'
    Write-Host    '    wix extension add -g WixToolset.UI.wixext'
    Write-Host    'The published build is ready in .\publish — only the MSI step was skipped.'
    return
}

New-Item -ItemType Directory -Force -Path dist | Out-Null

Write-Host '== Building MSI ==' -ForegroundColor Cyan
# Build from installer\ so the .wxs relative source paths (..\publish, ..\lyricsonthego.ico,
# wix-*.bmp) all resolve consistently to the working directory.
Push-Location $PSScriptRoot
try {
    wix build LyricsOnTheGo.wxs -arch x64 -ext WixToolset.UI.wixext `
        -d ProductVersion=$version `
        -o ..\dist\LyricsOnTheGo-$version.msi
    if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
}
finally { Pop-Location }

Write-Host '== Done. MSI is in .\dist ==' -ForegroundColor Green
Get-ChildItem dist\*.msi | Select-Object Name, Length
