# Fabrique l'installateur de MAUS : publie l'application autonome (.NET inclus, Windows x64),
# puis la compile avec Inno Setup 7 (ISCC.exe) en un seul fichier MAUS-<version>-installation.exe.
# Usage (PowerShell 5.1) : powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Iscc <chemin d'ISCC.exe>]
# Résultat : artifacts\installer\MAUS-<version>-installation.exe et son empreinte SHA-256.
param([string]$Iscc)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Version : celle de Directory.Build.props (la même que « À propos » et maus --version).
[xml]$props = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'Directory.Build.props')
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw "Version introuvable dans Directory.Build.props" }

if (-not $Iscc) {
    $candidates = @(
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe")
    $Iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc -or -not (Test-Path $Iscc)) { throw "ISCC.exe (Inno Setup 7) introuvable : installez Inno Setup 7 ou passez -Iscc <chemin>" }

$out = Join-Path $root 'artifacts\installer'
$files = Join-Path $out "fichiers-$version"
if (Test-Path $files) { Remove-Item -Recurse -Force $files }

# Application autonome : l'utilisateur n'a pas à installer .NET.
dotnet publish (Join-Path $root 'src\Maus.App\Maus.App.csproj') -c Release -r win-x64 --self-contained true -o $files --disable-build-servers -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué (code $LASTEXITCODE)" }

& $Iscc --quiet-progress "--define=AppVersion=$version" "--define=SourceDir=$files" "--define=OutputDir=$out" (Join-Path $PSScriptRoot 'MAUS.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup a échoué (code $LASTEXITCODE)" }

$setup = Join-Path $out "MAUS-$version-installation.exe"
$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash
"$hash  MAUS-$version-installation.exe" | Out-File -Encoding ascii (Join-Path $out "MAUS-$version-installation.exe.sha256")
"Installateur : $setup"
"Taille : {0:N1} Mo" -f ((Get-Item $setup).Length / 1MB)
"SHA-256 : $hash"
