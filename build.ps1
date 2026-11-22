param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

Write-Host "=== 1. Generating application icon ===" -ForegroundColor Cyan
powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\tools\generate_icon.ps1"

Write-Host "=== 2. Publishing self-contained single-file app (v$Version) ===" -ForegroundColor Cyan
$publishDir = Join-Path $PSScriptRoot "publish\win-x64"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish "$PSScriptRoot\src\FolderCleaner\FolderCleaner.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "=== 3. Building Windows Installer (Inno Setup 6) ===" -ForegroundColor Cyan
$isccPaths = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) not found."
}

& $iscc "/DMyAppVersion=$Version" "$PSScriptRoot\installer\FolderCleaner.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed." }

$setupFile = Join-Path $PSScriptRoot "dist\FolderCleaner_Setup.exe"
$sizeMb = [math]::Round((Get-Item $setupFile).Length / 1MB, 2)
Write-Host "=== Done! Installer ready: $setupFile ($sizeMb MB) ===" -ForegroundColor Green
