# Builds the distributable: the small framework-dependent wordlist2sql.exe plus
# the Native-AOT bootstrapper (wordlist2sql-launcher.exe) that checks for / installs
# the .NET 8 Desktop Runtime before launching the app.
#
# Usage:   .\build-dist.ps1            (x64, default)
#          .\build-dist.ps1 -Rid win-x86
#
# Output:  .\dist\  containing both exes, ready to zip and ship.

param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"

Write-Host "==> Cleaning $dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

# --- 1. Framework-dependent single-file app -------------------------------
Write-Host "==> Publishing wordlist2sql.exe (framework-dependent, single file)"
dotnet publish (Join-Path $root "wordlist2sql.csproj") `
    -c $Configuration -r $Rid --self-contained false -o $dist
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

# --- 2. Native-AOT launcher (needs the MSVC C++ toolchain) ----------------
Write-Host "==> Locating Visual C++ build environment (for AOT linking)"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found. Install the 'Desktop development with C++' workload." }

$vsPath = & $vswhere -latest -prerelease -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $vsPath) {
    $vsPath = & $vswhere -latest -prerelease -products * -property installationPath
}
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found under $vsPath" }

Write-Host "==> Importing MSVC environment from $vcvars"
cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path ("Env:" + $matches[1]) -Value $matches[2] }
}
$env:PATH = $env:PATH + ";" + (Split-Path $vswhere)

Write-Host "==> Publishing wordlist2sql-launcher.exe (Native AOT)"
dotnet publish (Join-Path $root "launcher\launcher.csproj") `
    -c $Configuration -r $Rid -o $dist
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed." }

# Drop stray symbol/config files; ship just the two exes.
Get-ChildItem $dist -Include *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==> Done. Distributable in: $dist"
Get-ChildItem $dist | Where-Object { -not $_.PSIsContainer } |
    Select-Object Name, @{N="SizeMB";E={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
Write-Host "Ship both files together. Users run wordlist2sql-launcher.exe."
