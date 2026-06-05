# Builds a SINGLE distributable exe: a Native-AOT bootstrapper with the
# framework-dependent wordlist2sql app embedded inside it. On launch it checks
# for the .NET 8 Desktop Runtime (installing it if missing), then runs the app.
#
# Usage:   .\build-dist.ps1            (x64, default)
#          .\build-dist.ps1 -Rid win-x86
#
# Output:  .\dist\wordlist2sql.exe     (one file, ~8.6 MB)

param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root     = $PSScriptRoot
$dist     = Join-Path $root "dist"
$stage    = Join-Path $root "launcher\embedded"     # holds the payload to embed
$appStage = Join-Path $env:TEMP "w2sql-appstage"

Write-Host "==> Cleaning output folders"
foreach ($d in @($dist, $stage, $appStage)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d | Out-Null
}

# --- 1. Framework-dependent single-file app (the payload) ------------------
Write-Host "==> Publishing the app (framework-dependent, single file)"
dotnet publish (Join-Path $root "wordlist2sql.csproj") `
    -c $Configuration -r $Rid --self-contained false -o $appStage
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

Copy-Item (Join-Path $appStage "wordlist2sql.exe") (Join-Path $stage "app.exe") -Force
$payloadMB = [math]::Round((Get-Item (Join-Path $stage "app.exe")).Length / 1MB, 1)
Write-Host "    embedded payload: app.exe ($payloadMB MB)"

# --- 2. Native-AOT launcher with the app embedded (needs MSVC C++ tools) ---
Write-Host "==> Locating Visual C++ build environment (for AOT linking)"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found. Install the 'Desktop development with C++' workload." }

$vsPath = & $vswhere -latest -prerelease -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $vsPath) { $vsPath = & $vswhere -latest -prerelease -products * -property installationPath }
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found under $vsPath" }

Write-Host "==> Importing MSVC environment"
cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path ("Env:" + $matches[1]) -Value $matches[2] }
}
$env:PATH = $env:PATH + ";" + (Split-Path $vswhere)

Write-Host "==> Publishing wordlist2sql.exe (Native AOT, app embedded)"
dotnet publish (Join-Path $root "launcher\launcher.csproj") `
    -c $Configuration -r $Rid -o $dist
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed." }

# --- 3. Tidy: ship only the single exe ------------------------------------
Get-ChildItem $dist -Exclude "wordlist2sql.exe" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $appStage -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==> Done. Single distributable:"
Get-ChildItem $dist | Select-Object Name, @{N="SizeMB";E={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
