# Images du paquet MSIX (Microsoft Store), tirées du logo du porteur : rien n'est redessiné, seulement recadré et
# redimensionné. Le « M » seul (src/Maus.App/Assets/maus-letter-256.png) pour les petites icônes, le logo entier
# (assets/logo/maus-logo.jpg) pour les tuiles. Résultat dans installer/msix/Images, avec les échelles de Windows.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File installer\msix\make-store-images.ps1

Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $PSScriptRoot 'Images'
New-Item -ItemType Directory -Force $out | Out-Null
$letter = [System.Drawing.Bitmap]::FromFile((Join-Path $root 'src\Maus.App\Assets\maus-letter-256.png'))
$logo = [System.Drawing.Bitmap]::FromFile((Join-Path $root 'assets\logo\maus-logo.jpg'))

function Save-Resized([System.Drawing.Image]$image, [System.Drawing.Rectangle]$box, [int]$width, [int]$height, [string]$name) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($image, (New-Object System.Drawing.Rectangle 0, 0, $width, $height), $box, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $bitmap.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# Zones à recadrer : le « M » entier ; un carré centré sur le logo ; une bande centrée sur le nom pour la tuile large.
$letterBox = New-Object System.Drawing.Rectangle 0, 0, $letter.Width, $letter.Height
$side = [Math]::Min($logo.Width, $logo.Height)
$squareBox = New-Object System.Drawing.Rectangle ([int](($logo.Width - $side) / 2)), ([int](($logo.Height - $side) / 2)), $side, $side
$wideHeight = [int]($logo.Width * 150 / 310)
$wideTop = [int]([Math]::Max(0, [Math]::Min($logo.Height - $wideHeight, ($logo.Height * 0.5) - ($wideHeight / 2))))
$wideBox = New-Object System.Drawing.Rectangle 0, $wideTop, $logo.Width, $wideHeight

foreach ($scale in 100, 125, 150, 200, 400) {
    $f = $scale / 100.0
    Save-Resized $letter $letterBox ([int](44 * $f)) ([int](44 * $f)) "Square44x44Logo.scale-$scale.png"
    Save-Resized $letter $letterBox ([int](50 * $f)) ([int](50 * $f)) "StoreLogo.scale-$scale.png"
    Save-Resized $logo $squareBox ([int](150 * $f)) ([int](150 * $f)) "Square150x150Logo.scale-$scale.png"
    Save-Resized $logo $squareBox ([int](71 * $f)) ([int](71 * $f)) "Square71x71Logo.scale-$scale.png"
    Save-Resized $logo $wideBox ([int](310 * $f)) ([int](150 * $f)) "Wide310x150Logo.scale-$scale.png"
}

# Icône de la barre des tâches et du menu Démarrer, aux tailles exactes, sur fond (« plated ») et sans plaque.
foreach ($size in 16, 24, 32, 48, 256) {
    Save-Resized $letter $letterBox $size $size "Square44x44Logo.targetsize-$size.png"
    Save-Resized $letter $letterBox $size $size "Square44x44Logo.targetsize-$($size)_altform-unplated.png"
}

$letter.Dispose()
$logo.Dispose()
"Images : $out ($((Get-ChildItem $out).Count) fichiers, logo source $($side) px)"
