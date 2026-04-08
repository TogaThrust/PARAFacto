#Requires -Version 5.1
# UTF-8: utiliser uniquement ASCII dans les chaines pour PowerShell 5.1 (Windows).
<#
.SYNOPSIS
  Genere l'installateur Windows unique PARAFactoNative_Installer.exe.

.DESCRIPTION
  Le script :
    1) publie PARAFactoNative (Release, win-x64, self-contained) dans installer_output (PARAFactoNative.exe + dependances)
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

function Get-ProjectVersion {
    param([string]$CsprojPath)
    $raw = Get-Content -Path $CsprojPath -Raw -Encoding UTF8
    $m = [regex]::Match($raw, "<Version>\s*([^<\s]+)\s*</Version>", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $m.Success) {
        return [Version]::new(1, 0, 0)
    }
    $txt = $m.Groups[1].Value.Trim()
    $v = $null
    if (-not [Version]::TryParse($txt, [ref]$v)) {
        throw "Version invalide dans le csproj: '$txt'. Format attendu: major.minor.patch"
    }
    return $v
}

function Format-Version3 {
    param([Version]$VersionValue)
    $build = if ($VersionValue.Build -ge 0) { $VersionValue.Build } else { 0 }
    return "{0}.{1}.{2}" -f $VersionValue.Major, $VersionValue.Minor, $build
}

function Increment-VersionPatch {
    param([Version]$VersionValue)
    $build = if ($VersionValue.Build -ge 0) { $VersionValue.Build } else { 0 }
    return [Version]::new($VersionValue.Major, $VersionValue.Minor, $build + 1)
}

function Set-ProjectVersion {
    param(
        [string]$CsprojPath,
        [string]$VersionText
    )
    $raw = Get-Content -Path $CsprojPath -Raw -Encoding UTF8
    if ([regex]::IsMatch($raw, "<Version>\s*[^<]+\s*</Version>", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        $updated = [regex]::Replace(
            $raw,
            "<Version>\s*[^<]+\s*</Version>",
            "<Version>$VersionText</Version>",
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    else {
        $updated = $raw -replace "</PropertyGroup>", "    <Version>$VersionText</Version>`r`n  </PropertyGroup>"
    }
    # PS 5.1: Set-Content -Encoding UTF8 ajoute un BOM; MSBuild n'en a pas besoin (Git plus propre).
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($CsprojPath, $updated, $utf8NoBom)
}

function Set-AppVersionJson {
    param(
        [string]$JsonPath,
        [string]$VersionText
    )
    $dir = Split-Path -Parent $JsonPath
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $content = "{`n  `"latestVersion`": `"$VersionText`"`n}`n"
    Set-Content -Path $JsonPath -Value $content -Encoding ASCII
}

$scriptDir = $PSScriptRoot
$csproj = Resolve-Project -BaseDir $scriptDir
if (-not $csproj) {
    throw "PARAFactoNative.csproj introuvable. Placez CreerInstalleur.ps1 a la racine PARAFacto ou dans PARAFactoNative."
}

$projectDir = Split-Path $csproj -Parent
$repoRoot = Split-Path $projectDir -Parent
$installerDir = Join-Path $projectDir "installer_output"
$publishDir = $installerDir
$issPath = Join-Path $env:TEMP ("PARAFactoNative_Setup_{0}.iss" -f [Guid]::NewGuid().ToString("N"))
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
    $currentVersion = Get-ProjectVersion -CsprojPath $csproj
    $nextVersion = Increment-VersionPatch -VersionValue $currentVersion
    $AppVersion = Format-Version3 -VersionValue $nextVersion
}
else {
    $parsed = $null
    if (-not [Version]::TryParse($AppVersion.Trim(), [ref]$parsed)) {
        throw "AppVersion invalide: '$AppVersion'. Format attendu: major.minor.patch"
    }
    $AppVersion = Format-Version3 -VersionValue $parsed
}

Set-ProjectVersion -CsprojPath $csproj -VersionText $AppVersion
Set-AppVersionJson -JsonPath (Join-Path $repoRoot "subscription-site\public\app-version.json") -VersionText $AppVersion
Set-AppVersionJson -JsonPath (Join-Path $projectDir "subscription-site\public\app-version.json") -VersionText $AppVersion

Write-Host "Projet       : $csproj"
Write-Host "Publish dir  : $publishDir"
Write-Host "Installer dir: $installerDir"
Write-Host "Version      : $AppVersion"
Write-Host ""

if (Test-Path $installerDir) {
    Remove-Item -Recurse -Force $installerDir
}
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

try {
    & $iscc $issPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation Inno Setup echouee (code $LASTEXITCODE)."
    }
}
finally {
    if (Test-Path $issPath) {
        Remove-Item -Force $issPath -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path $installerExe)) {
    throw "Installateur introuvable: $installerExe"
}

Write-Host ""
Write-Host "OK - Installateur genere."
Write-Host "  Fichier : $installerExe"
Write-Host "  Pensez a publier ce .exe comme asset GitHub Release."
