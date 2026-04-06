#Requires -Version 5.1
<#
.SYNOPSIS
  Publie PARAFactoNative (Release, win-x64, self-contained) pour empaquetage installateur.

.DESCRIPTION
  Placez ce script soit :
  - à la racine du dépôt GitHub PARAFacto (à côté du dossier PARAFactoNative), soit
  - dans le dossier PARAFactoNative (même niveau que PARAFactoNative.csproj).

  Sortie par défaut : PARAFactoNative\publish_output\win-x64\
  (subscription_config.json est copié par le csproj à côté de l’exe.)

  Pour un .exe « installateur » unique (Inno Setup, WiX, etc.), pointez votre outil
  vers le dossier de sortie ou vers PARAFactoNative.exe généré.
#>
$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot
$csproj = $null
foreach ($candidate in @(
        (Join-Path $scriptDir "PARAFactoNative\PARAFactoNative.csproj"),
        (Join-Path $scriptDir "PARAFactoNative.csproj")
    )) {
    if (Test-Path $candidate) {
        $csproj = $candidate
        break
    }
}

if (-not $csproj) {
    throw "PARAFactoNative.csproj introuvable. Copiez CreerInstalleur.ps1 à la racine du repo PARAFacto ou dans le dossier PARAFactoNative."
}

$projectDir = Split-Path $csproj -Parent
$outDir = Join-Path $projectDir "publish_output\win-x64"

Write-Host "Projet : $csproj"
Write-Host "Sortie : $outDir"
Write-Host ""

if (Test-Path $outDir) {
    Remove-Item -Recurse -Force $outDir
}

dotnet publish $csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish a échoué (code $LASTEXITCODE)."
}

$exe = Join-Path $outDir "PARAFactoNative.exe"
if (-not (Test-Path $exe)) {
    throw "PARAFactoNative.exe introuvable dans $outDir"
}

Write-Host ""
Write-Host "OK — Build terminé."
Write-Host "  Exe : $exe"
Write-Host "  Vérifiez subscription_config.json à côté de l’exe (skipValidation, URLs)."
Write-Host "  Prochaine étape : empaqueter ce dossier avec votre outil d’installation (ex. Inno Setup)."
