#Requires -Version 5.1
# UTF-8: utiliser uniquement ASCII dans les chaines pour PowerShell 5.1 (Windows).
<#
.SYNOPSIS
  Genere l'installateur Windows unique PARAFactoNative_Installer.exe.

.DESCRIPTION
  Le script :
    1) publie PARAFactoNative (Release, win-x64, self-contained) dans publish_output\win-x64
    2) compile un installateur Inno Setup vers installer_output\PARAFactoNative_Installer.exe

  Prerequis :
    - .NET SDK installe (commande dotnet)
    - Inno Setup 6 installe (ISCC.exe)
#>

param(
    [string]$AppVersion = "",
    [string]$Publisher = "PARAFacto",
    [string]$AppId = "{{95DA48C0-A83B-4F53-8A8A-7B81B81CC8E9}}"
)

$ErrorActionPreference = "Stop"

function Resolve-IsccPath {
    $fromPath = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
    if ($fromPath -and $fromPath.Source) {
        return $fromPath.Source
    }

    $candidates = @(
        (Join-Path $env:LocalAppData "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) {
            return $c
        }
    }
    return $null
}

function Resolve-Project {
    param([string]$BaseDir)
    foreach ($candidate in @(
            (Join-Path $BaseDir "PARAFactoNative\PARAFactoNative.csproj"),
            (Join-Path $BaseDir "PARAFactoNative.csproj")
        )) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }
    return $null
}

$scriptDir = $PSScriptRoot
$csproj = Resolve-Project -BaseDir $scriptDir
if (-not $csproj) {
    throw "PARAFactoNative.csproj introuvable. Placez CreerInstalleur.ps1 a la racine PARAFacto ou dans PARAFactoNative."
}

$projectDir = Split-Path $csproj -Parent
$publishDir = Join-Path $projectDir "publish_output\win-x64"
$installerDir = Join-Path $projectDir "installer_output"
$issPath = Join-Path $installerDir "PARAFactoNative_Setup.iss"
$installerExe = Join-Path $installerDir "PARAFactoNative_Installer.exe"
$iscc = Resolve-IsccPath
if (-not $iscc) {
    throw @"
Inno Setup (ISCC.exe) introuvable.
Installez Inno Setup 6 puis relancez le script:
https://jrsoftware.org/isdl.php
"@
}

if ([string]::IsNullOrWhiteSpace($AppVersion)) {
    $AppVersion = Get-Date -Format "yyyy.MM.dd.HHmm"
}

Write-Host "Projet       : $csproj"
Write-Host "Publish dir  : $publishDir"
Write-Host "Installer dir: $installerDir"
Write-Host "Version      : $AppVersion"
Write-Host ""

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
if (Test-Path $installerDir) {
    Remove-Item -Recurse -Force $installerDir
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

dotnet publish $csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish a echoue (code $LASTEXITCODE)."
}

$mainExe = Join-Path $publishDir "PARAFactoNative.exe"
if (-not (Test-Path $mainExe)) {
    throw "PARAFactoNative.exe introuvable dans $publishDir"
}

$innoScript = @"
#define MyAppName "PARAFacto Native"
#define MyAppVersion "$AppVersion"
#define MyAppPublisher "$Publisher"
#define MyAppExeName "PARAFactoNative.exe"

[Setup]
AppId=$AppId
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PARAFacto Native
DefaultGroupName=PARAFacto Native
OutputDir=$installerDir
OutputBaseFilename=PARAFactoNative_Installer
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Creer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

[Files]
Source: "$publishDir\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PARAFacto Native"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\PARAFacto Native"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Lancer PARAFacto Native"; Flags: nowait postinstall skipifsilent
"@

Set-Content -Path $issPath -Value $innoScript -Encoding ASCII

& $iscc $issPath | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Compilation Inno Setup echouee (code $LASTEXITCODE)."
}

if (-not (Test-Path $installerExe)) {
    throw "Installateur introuvable: $installerExe"
}

Write-Host ""
Write-Host "OK - Installateur genere."
Write-Host "  Fichier : $installerExe"
Write-Host "  Pensez a publier ce .exe comme asset GitHub Release."
