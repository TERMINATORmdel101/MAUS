# Images de la fiche du Microsoft Store, tirées du logo du porteur (assets/logo/maus-logo.jpg, rien n'est redessiné) :
# image de zone 1:1 (2160 × 2160) et affiche 9:16 (1440 × 2160 : le logo carré au centre, la couleur du mur au-dessus
# et au-dessous, prise sur ses bords). Résultat dans publish\store-<version>.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File installer\msix\make-listing-images.ps1 -Out publish\store-0.7.5

param([string]$Out = 'publish\store')

Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$target = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
New-Item -ItemType Directory -Force $target | Out-Null
$logo = [System.Drawing.Bitmap]::FromFile((Join-Path $root 'assets\logo\maus-logo.jpg'))
$side = [Math]::Min($logo.Width, $logo.Height)
$square = New-Object System.Drawing.Rectangle ([int](($logo.Width - $side) / 2)), ([int](($logo.Height - $side) / 2)), $side, $side

function New-Image([int]$width, [int]$height) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    return @($bitmap, $g)
}

# Carré 1:1.
$bitmap, $g = New-Image 2160 2160
$g.DrawImage($logo, (New-Object System.Drawing.Rectangle 0, 0, 2160, 2160), $square, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$bitmap.Save((Join-Path $target 'store-carre-2160.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

# Couleur moyenne d'une bande de lignes du logo (mur en haut, sol en bas).
function Average-Color([int]$y0, [int]$rows) {
    $r = 0; $gr = 0; $b = 0; $n = 0
    for ($y = $y0; $y -lt $y0 + $rows; $y++) {
        for ($x = $square.X; $x -lt $square.Right; $x += 8) {
            $c = $logo.GetPixel($x, $y); $r += $c.R; $gr += $c.G; $b += $c.B; $n++
        }
    }
    return [System.Drawing.Color]::FromArgb([int]($r / $n), [int]($gr / $n), [int]($b / $n))
}

function Darker([System.Drawing.Color]$c, [double]$f) {
    return [System.Drawing.Color]::FromArgb([int]($c.R * $f), [int]($c.G * $f), [int]($c.B * $f))
}

# Affiche 9:16 : le logo carré au centre ; au-dessus et au-dessous, un dégradé uni de la couleur du mur et du sol.
$bitmap, $g = New-Image 1440 2160
$top = [int]((2160 - 1440) / 2)
$wall = Average-Color $square.Y 24
$floor = Average-Color ($square.Bottom - 24) 24
$upper = New-Object System.Drawing.Rectangle 0, 0, 1440, ($top + 2)
$lower = New-Object System.Drawing.Rectangle 0, ($top + 1438), 1440, ($top + 2)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $upper, (Darker $wall 0.85), $wall, 90
$g.FillRectangle($brush, $upper); $brush.Dispose()
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $lower, $floor, (Darker $floor 0.85), 90
$g.FillRectangle($brush, $lower); $brush.Dispose()
$g.DrawImage($logo, (New-Object System.Drawing.Rectangle 0, $top, 1440, 1440), $square, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$bitmap.Save((Join-Path $target 'store-affiche-1440x2160.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
$logo.Dispose()
"Images de la fiche : $target"
