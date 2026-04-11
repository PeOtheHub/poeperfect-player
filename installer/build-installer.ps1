Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $root "src\PoePerfect.Player.Windows\PoePerfect.Player.Windows.csproj"
$publishDir = Join-Path $root "publish\win-x64"
$distDir = Join-Path $root "dist"
$installerScript = Join-Path $PSScriptRoot "PoePerfectPlayer.iss"
$satelliteLanguages = @("sv", "en")

$candidateIsccPaths = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$isccPath = $candidateIsccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $isccPath) {
    throw "Kunde inte hitta ISCC.exe. Installera Inno Setup 6."
}

[xml]$projectXml = Get-Content $projectFile
$propertyGroup = $projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
$appVersion = if ($propertyGroup -and $propertyGroup.Version) { [string]$propertyGroup.Version } else { "1.0.0" }

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

Write-Host "Publicerar PoePerfect Player..." -ForegroundColor Cyan
$publishArgs = @(
    "publish", $projectFile,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:Platform=x64",
    "-p:VlcWindowsX86Enabled=false",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-o", $publishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish misslyckades med exitkod $LASTEXITCODE."
}

Write-Host "Rensar distributionsfiler..." -ForegroundColor Cyan

$x86VlcDir = Join-Path $publishDir "libvlc\win-x86"
if (Test-Path $x86VlcDir) {
    Remove-Item $x86VlcDir -Recurse -Force
}

Get-ChildItem $publishDir -Recurse -File -Include *.pdb, *.lib -ErrorAction SilentlyContinue |
    Remove-Item -Force

Get-ChildItem $publishDir -Directory | Where-Object {
    $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' -and $_.Name -notin $satelliteLanguages
} | Remove-Item -Recurse -Force

Write-Host "Bygger Setup.exe..." -ForegroundColor Cyan
& $isccPath `
    "/DMyAppVersion=$appVersion" `
    "/DMyPublishDir=$publishDir" `
    "/DMyOutputDir=$distDir" `
    $installerScript

$setupExe = Join-Path $distDir "PoePerfectPlayer-Setup.exe"
if (-not (Test-Path $setupExe)) {
    throw "Setup.exe skapades inte som förväntat."
}

Write-Host ""
Write-Host "Klar:" -ForegroundColor Green
Write-Host $setupExe
