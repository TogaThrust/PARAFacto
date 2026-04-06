#Requires -Version 5.1
# UTF-8: utiliser uniquement ASCII dans les chaines pour PowerShell 5.1 (Windows).
<#
.SYNOPSIS
  Publie PARAFactoNative (Release, win-x64, self-contained) pour empaquetage installateur.

.DESCRIPTION
  Placer ce script soit a la racine du depot PARAFacto (a cote de PARAFactoNative),
  soit dans le dossier PARAFactoNative (meme niveau que PARAFactoNative.csproj).

  Sortie : PARAFactoNative\publish_output\win-x64\
  subscription_config.json est copie par le csproj a cote de l'exe.

  Pour un installateur (Inno Setup, WiX, etc.), pointer l'outil vers ce dossier ou PARAFactoNative.exe.
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
    throw "PARAFactoNative.csproj introuvable. Placer CreerInstalleur.ps1 a la racine PARAFacto ou dans PARAFactoNative."
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
    throw "dotnet publish a echoue (code $LASTEXITCODE)."
}

$exe = Join-Path $outDir "PARAFactoNative.exe"
if (-not (Test-Path $exe)) {
    throw "PARAFactoNative.exe introuvable dans $outDir"
}

Write-Host ""
Write-Host "OK - Build termine."
Write-Host "  Exe : $exe"
Write-Host "  Verifiez subscription_config.json a cote de l'exe (skipValidation, URLs)."
Write-Host "  Etape suivante : empaqueter ce dossier avec votre outil (ex. Inno Setup)."
