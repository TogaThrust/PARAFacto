#Requires -Version 5.1
# UTF-8: utiliser uniquement ASCII dans les chaines pour PowerShell 5.1 (Windows).
<#
.SYNOPSIS
  Genere l'installateur Windows unique PARAFactoNative_Installer.exe.

.DESCRIPTION
  Le script :
    1) publie PARAFactoNative (Release, win-x64, self-contained) dans publish_output\win-x64
    2) compile l'installateur Inno : seul PARAFactoNative_Installer.exe est ecrit dans installer_output

  Prerequis :
    - .NET SDK installe (commande dotnet)
    - Inno Setup 6 installe (ISCC.exe) — sauf si -SiteSeulement

  -SiteSeulement :
    Met a jour <Version> dans le csproj et les deux app-version.json, puis quitte sans publish ni installateur.

  Version :
    Sans -AppVersion, le script demande le numero (une seule saisie, ecrit partout).
    Avec -AppVersion "1.2.3", aucune question (CI / scripts).

  app-version.json :
    Contient latestVersion, installerUrl (exe GitHub par tag), downloadPageUrl (page Netlify avec consignes).
    Apres publication : creez sur GitHub une release avec le tag vVERSION (ex. v1.2.3) et joignez
    PARAFactoNative_Installer.exe, sinon le lien de telechargement renvoie 404 ou un ancien fichier.
#>

param(
    [string]$AppVersion = "",
    [string]$Publisher = "PARAFacto",
    [string]$AppId = "{{95DA48C0-A83B-4F53-8A8A-7B81B81CC8E9}}",
    [switch]$SiteSeulement
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

function Resolve-AppVersionText {
    param(
        [string]$FromParam,
        [string]$CsprojPath
    )
    if (-not [string]::IsNullOrWhiteSpace($FromParam)) {
        $parsed = $null
        if (-not [Version]::TryParse($FromParam.Trim(), [ref]$parsed)) {
            throw "AppVersion invalide: '$FromParam'. Format attendu: major.minor.patch"
        }
        return (Format-Version3 -VersionValue $parsed)
    }
    $current = Get-ProjectVersion -CsprojPath $CsprojPath
    $hint = Format-Version3 -VersionValue $current
    while ($true) {
        $line = Read-Host "Numero de version pour cette publication (ex. 1.0.33). Actuelle dans le projet : $hint"
        if ([string]::IsNullOrWhiteSpace($line)) {
            Write-Warning "Saisie vide : reessayez ou Ctrl+C pour annuler."
            continue
        }
        $parsed = $null
        if (-not [Version]::TryParse($line.Trim(), [ref]$parsed)) {
            Write-Warning "Format invalide : major.minor.patch (ex. 1.0.33)."
            continue
        }
        return (Format-Version3 -VersionValue $parsed)
    }
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
    # URL par tag GitHub (pas "releases/latest" : sinon on telecharge encore l'exe de l'ancienne release).
    $installerUrl = "https://github.com/TogaThrust/PARAFacto/releases/download/v$VersionText/PARAFactoNative_Installer.exe"
    $downloadPageUrl = "https://parafacto.netlify.app/"
    $content = "{`n  `"latestVersion`": `"$VersionText`",`n  `"installerUrl`": `"$installerUrl`",`n  `"downloadPageUrl`": `"$downloadPageUrl`"`n}`n"
    Set-Content -Path $JsonPath -Value $content -Encoding ASCII
}

function Read-LatestVersionFromJsonFile {
    param([string]$JsonPath)
    if (-not (Test-Path $JsonPath)) {
        return "(fichier absent)"
    }
    $t = Get-Content -Path $JsonPath -Raw -Encoding UTF8
    $m = [regex]::Match($t, '"latestVersion"\s*:\s*"([^"]+)"')
    if (-not $m.Success) {
        return "(latestVersion introuvable)"
    }
    return $m.Groups[1].Value.Trim()
}

function Read-VersionFromCsprojFile {
    param([string]$CsprojPath)
    if (-not (Test-Path $CsprojPath)) {
        return "(fichier absent)"
    }
    $raw = Get-Content -Path $CsprojPath -Raw -Encoding UTF8
    $m = [regex]::Match($raw, "<Version>\s*([^<\s]+)\s*</Version>", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $m.Success) {
        return "(balise Version introuvable)"
    }
    return $m.Groups[1].Value.Trim()
}

function Show-VersionVerificationSummary {
    param(
        [string]$CsprojPath,
        [string]$JsonRootPath,
        [string]$JsonProjectPath,
        [string]$Title = "Controle des 3 fichiers version (lecture disque)"
    )
    $vProj = Read-VersionFromCsprojFile -CsprojPath $CsprojPath
    $vJsonRoot = Read-LatestVersionFromJsonFile -JsonPath $JsonRootPath
    $vJsonNative = Read-LatestVersionFromJsonFile -JsonPath $JsonProjectPath
    Write-Host ""
    Write-Host "=== $Title ==="
    Write-Host "  1) csproj  <Version>     : $vProj"
    Write-Host "     $CsprojPath"
    Write-Host "  2) app-version (site racine repo, Netlify) : $vJsonRoot"
    Write-Host "     $JsonRootPath"
    Write-Host "  3) app-version (copie PARAFactoNative)     : $vJsonNative"
    Write-Host "     $JsonProjectPath"
    $ok = ($vProj -eq $vJsonRoot) -and ($vProj -eq $vJsonNative) -and ($vJsonRoot -eq $vJsonNative)
    if ($ok) {
        Write-Host "  => Les trois valeurs concordent."
    }
    else {
        Write-Warning "  => Divergence entre fichiers : verifiez avant commit / deploy."
    }
    Write-Host "=============================================="
    Write-Host ""
}

$scriptDir = $PSScriptRoot
$csproj = Resolve-Project -BaseDir $scriptDir
if (-not $csproj) {
    throw "PARAFactoNative.csproj introuvable. Placez CreerInstalleur.ps1 a la racine PARAFacto ou dans PARAFactoNative."
}

$projectDir = Split-Path $csproj -Parent
$repoRoot = Split-Path $projectDir -Parent
$publishDir = Join-Path $projectDir "publish_output\win-x64"
$installerDir = Join-Path $projectDir "installer_output"
$issPath = Join-Path $env:TEMP ("PARAFactoNative_Setup_{0}.iss" -f [Guid]::NewGuid().ToString("N"))
$installerExe = Join-Path $installerDir "PARAFactoNative_Installer.exe"

$AppVersion = Resolve-AppVersionText -FromParam $AppVersion -CsprojPath $csproj

$appVersionJsonRepoRoot = Join-Path $repoRoot "subscription-site\public\app-version.json"
$appVersionJsonNativeCopy = Join-Path $projectDir "subscription-site\public\app-version.json"

Set-ProjectVersion -CsprojPath $csproj -VersionText $AppVersion
Set-AppVersionJson -JsonPath $appVersionJsonRepoRoot -VersionText $AppVersion
Set-AppVersionJson -JsonPath $appVersionJsonNativeCopy -VersionText $AppVersion

Write-Host "Projet  : $csproj"
Write-Host "Version : $AppVersion"
Write-Host ""

if ($SiteSeulement) {
    Write-Host "OK - Site uniquement : csproj + app-version.json (racine repo et PARAFactoNative) mis a jour."
    Write-Host "Ensuite : git add / commit / push ; laisser Netlify redeployer le site (dossier subscription-site)."
    Show-VersionVerificationSummary -CsprojPath $csproj -JsonRootPath $appVersionJsonRepoRoot -JsonProjectPath $appVersionJsonNativeCopy -Title "Fin (site uniquement) - controle des 3 fichiers"
    exit 0
}

$iscc = Resolve-IsccPath
if (-not $iscc) {
    throw @"
Inno Setup (ISCC.exe) introuvable.
Installez Inno Setup 6 puis relancez le script:
https://jrsoftware.org/isdl.php
"@
}

Write-Host "Publish dir  : $publishDir"
Write-Host "Installer dir: $installerDir"
Write-Host ""

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

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

# Inno interprete \t, \n, etc. dans les chemins (ex. C:\Users\togat\...) : doubler chaque \ pour le fichier .iss.
function Escape-InnoSetupPath {
    param([string]$Path)
    return $Path.Replace('\', '\\')
}

$publishDirInno = Escape-InnoSetupPath $publishDir
$installerDirInno = Escape-InnoSetupPath $installerDir

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
OutputDir=$installerDirInno
OutputBaseFilename=PARAFactoNative_Installer
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Creer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

[Files]
Source: "$publishDirInno\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

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
Write-Host "  App (publish) : $publishDir"
Write-Host "  Installateur  : $installerExe"
Write-Host "  Pensez a publier l'installateur .exe comme asset GitHub Release."
Show-VersionVerificationSummary -CsprojPath $csproj -JsonRootPath $appVersionJsonRepoRoot -JsonProjectPath $appVersionJsonNativeCopy -Title "Fin (installateur) - controle des 3 fichiers"
