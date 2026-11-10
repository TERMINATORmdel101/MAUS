# Fabrique le paquet MSIX de MAUS pour le Microsoft Store : application autonome (win-x64, .NET inclus), manifeste
# (installer/msix/AppxManifest.xml), images (installer/msix/Images, voir make-store-images.ps1), index des ressources
# (makepri) puis paquet (makeappx). Le paquet n'est pas signé : le Store le signe lui-même après la certification.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File installer\msix\build-msix.ps1
#
# Résultat : artifacts\msix\MAUS-<version>.msix, son empreinte (.sha256) et le dossier des fichiers, artifacts\msix\files.
#
# Le paquet n'est fabriqué que si tout compile sans avertissement et si tous les tests passent, avec exactement les
# bibliothèques des fichiers packages.lock.json. Sur GitHub : .github/workflows/build-msix.yml, qui ajoute une attestation
# de provenance. Un paquet déjà fabriqué pour cette version n'est jamais écrasé sans -Overwrite : c'est peut-être celui
# qui est soumis à Microsoft.
param([string]$OutputFolder, [switch]$Overwrite)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# Version de Directory.Build.props, au format du Store (x.y.z.0).
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw "Version introuvable dans Directory.Build.props" }
$msixVersion = "$version.0"

$out = if ($OutputFolder) { $OutputFolder } else { Join-Path $root 'artifacts\msix' }
$files = Join-Path $out 'files'
$staging = Join-Path $out 'pri'
if ((Test-Path (Join-Path $out "MAUS-$version.msix")) -and -not $Overwrite) {
    throw "Un paquet $version existe déjà dans $out : il n'est pas écrasé. Changez la version, ou relancez avec -Overwrite si vous êtes sûr de vous."
}

if (git -C $root status --porcelain) { Write-Warning "Modifications non enregistrées : ce paquet ne correspondra à aucun commit." }

# 0. Compilation sans avertissement et tests : sinon, pas de paquet.
$solution = Join-Path $root 'Maus.slnx'
dotnet build $solution -c Release -warnaserror --disable-build-servers -nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw "Compilation en échec (code $LASTEXITCODE) : pas de paquet" }
dotnet test $solution -c Release --no-build -nologo
if ($LASTEXITCODE -ne 0) { throw "Tests en échec (code $LASTEXITCODE) : pas de paquet" }

# Seulement ce que ce script fabrique : le dossier de sortie peut contenir d'autres fichiers.
foreach ($made in @($files, $staging, (Join-Path $out 'priconfig.xml'))) { if (Test-Path $made) { Remove-Item -Recurse -Force $made } }
New-Item -ItemType Directory -Force $files, $staging | Out-Null

# 1. Application autonome.
dotnet publish (Join-Path $root 'src\Maus.App\Maus.App.csproj') -c Release -r win-x64 --self-contained true -o $files --disable-build-servers -nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué (code $LASTEXITCODE)" }

# Empreinte du programme, affichée par « Vérifier mon installation » : à publier avec la version.
$program = (Get-FileHash -Algorithm SHA256 (Join-Path $files 'MAUS.dll')).Hash
"$program  MAUS.dll" | Out-File -Encoding ascii (Join-Path $out "MAUS-$version-programme.sha256")

# 2. Manifeste et images.
$manifest = (Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'AppxManifest.xml')).Replace('@VERSION@', $msixVersion)
[System.IO.File]::WriteAllText((Join-Path $files 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding $false))
Copy-Item -Recurse (Join-Path $PSScriptRoot 'Images') (Join-Path $files 'Images')

# 3. Outils de Microsoft (paquet NuGet officiel, restauré dans le cache NuGet).
$project = Join-Path $PSScriptRoot 'msix-tools.csproj'
$line = dotnet msbuild $project -restore -t:ShowToolsPath -nologo -v:m | Select-String 'MSIXTOOLS='
if (-not $line) { throw "Outils MSIX introuvables (restauration de Microsoft.Windows.SDK.BuildTools)" }
$tools = $line.ToString().Split('=', 2)[1].Trim()
$makeappx = Get-ChildItem -Recurse -Filter makeappx.exe (Join-Path $tools 'bin') | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
$makepri = Get-ChildItem -Recurse -Filter makepri.exe (Join-Path $tools 'bin') | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
if (-not $makeappx -or -not $makepri) { throw "makeappx.exe ou makepri.exe introuvable dans $tools" }

# 4. Index des ressources (images aux différentes échelles), calculé sur les images seules : pas sur les 200 Mo de MAUS.
Copy-Item -Recurse (Join-Path $PSScriptRoot 'Images') (Join-Path $staging 'Images')
Copy-Item (Join-Path $files 'AppxManifest.xml') $staging
$config = Join-Path $out 'priconfig.xml'
& $makepri.FullName createconfig /cf $config /dq fr-FR_en-US_es-ES /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig a échoué (code $LASTEXITCODE)" }
# Un seul paquet (pas de paquets de ressources par langue ou par échelle) : un seul index, resources.pri.
[xml]$priConfig = Get-Content $config
foreach ($node in @($priConfig.SelectNodes('//packaging'))) { [void]$node.ParentNode.RemoveChild($node) }
$priConfig.Save($config)
& $makepri.FullName new /pr $staging /cf $config /mn (Join-Path $staging 'AppxManifest.xml') /of (Join-Path $files 'resources.pri') /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri new a échoué (code $LASTEXITCODE)" }

# 5. Paquet.
$package = Join-Path $out "MAUS-$version.msix"
& $makeappx.FullName pack /d $files /p $package /o
if ($LASTEXITCODE -ne 0) { throw "makeappx a échoué (code $LASTEXITCODE)" }
$hash = (Get-FileHash -Algorithm SHA256 $package).Hash
"$hash  MAUS-$version.msix" | Out-File -Encoding ascii "$package.sha256"
"Paquet : $package ($([Math]::Round((Get-Item $package).Length / 1MB, 1)) Mo, version $msixVersion)"
"SHA-256 : $hash"
