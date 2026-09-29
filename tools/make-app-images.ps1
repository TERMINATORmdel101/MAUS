# Fabrique les images de l'application à partir du logo du porteur (assets/logo/maus-logo.jpg), sous Windows.
# Rien n'est redessiné : recadrage, marge couleur du mur, redimensionnement et coins arrondis seulement.
#
#   powershell -ExecutionPolicy Bypass -File tools/make-app-images.ps1
#
# - src/Maus.App/Assets/maus-letter-256.png : le M entier, centré sur la couleur du mur (petites tailles de l'icône).
# - src/Maus.App/Assets/maus.ico : le M de 16 à 48 px (lisible dans la barre des tâches), le logo entier de 64 à 256 px
#   (accord du porteur du 29/09/2026). Tailles 16 à 128 en BMP 32 bits (WIC refuse les petites tailles en PNG), 256 en PNG.
# - src/Maus.App/Assets/maus-logo-512.png : le logo entier pour « À propos ».
# - src/Maus.App/Assets/splash.png : la bannière aux coins arrondis, fond transparent, pour l'écran de démarrage.
# - src/Maus.Core/Reporting/maus-report-logo.jpg : petite bannière pour l'en-tête du rapport HTML.

Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$logo = [System.Drawing.Bitmap]::FromFile((Join-Path $root 'assets\logo\maus-logo.jpg'))
$assets = Join-Path $root 'src\Maus.App\Assets'

function New-Canvas([int]$width, [int]$height) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    return @($bitmap, $g)
}

function Crop([System.Drawing.Image]$image, [System.Drawing.Rectangle]$box, [int]$width, [int]$height) {
    $canvas, $g = New-Canvas $width $height
    $g.DrawImage($image, (New-Object System.Drawing.Rectangle 0, 0, $width, $height), $box, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    return $canvas
}

# Le M entier : x 250 à 720 (le A commence vers 727), y 720 à 1320, dans l'image de 2048 px. Pour faire un carré sans
# prendre le A, les bords du mur (colonnes x = 250 et x = 719) sont prolongés à gauche et à droite.
$side = 600; $mWidth = 470; $margin = ($side - $mWidth) / 2
$letter, $g = New-Canvas $side $side
$g.DrawImage($logo, (New-Object System.Drawing.Rectangle 0, 0, $margin, $side), (New-Object System.Drawing.Rectangle 250, 720, 1, 600), [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawImage($logo, (New-Object System.Drawing.Rectangle ($side - $margin), 0, $margin, $side), (New-Object System.Drawing.Rectangle 719, 720, 1, 600), [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawImage($logo, (New-Object System.Drawing.Rectangle $margin, 0, $mWidth, $side), (New-Object System.Drawing.Rectangle 250, 720, $mWidth, 600), [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$letter256 = Crop $letter (New-Object System.Drawing.Rectangle 0, 0, $side, $side) 256 256
$letter256.Save((Join-Path $assets 'maus-letter-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# Logo entier pour « À propos ».
$full512 = Crop $logo (New-Object System.Drawing.Rectangle 0, 0, 2048, 2048) 512 512
$full512.Save((Join-Path $assets 'maus-logo-512.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# Bannière (même recadrage que la barre de navigation) aux coins arrondis, fond transparent.
$bannerBox = New-Object System.Drawing.Rectangle 100, 540, 1848, 900
$splashW = 560; $splashH = [int][Math]::Round(560 * 900 / 1848)
$banner = Crop $logo $bannerBox $splashW $splashH
$splash, $g = New-Canvas $splashW $splashH
$radius = 28
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, $radius * 2, $radius * 2, 180, 90)
$path.AddArc($splashW - $radius * 2 - 1, 0, $radius * 2, $radius * 2, 270, 90)
$path.AddArc($splashW - $radius * 2 - 1, $splashH - $radius * 2 - 1, $radius * 2, $radius * 2, 0, 90)
$path.AddArc(0, $splashH - $radius * 2 - 1, $radius * 2, $radius * 2, 90, 90)
$path.CloseFigure()
$g.Clear([System.Drawing.Color]::Transparent)
$brush = New-Object System.Drawing.TextureBrush $banner
$g.FillPath($brush, $path)
$g.Dispose()
$splash.Save((Join-Path $assets 'splash.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# Petite bannière du rapport HTML (JPEG, embarquée en base64).
$report = Crop $logo $bannerBox 360 ([int][Math]::Round(360 * 900 / 1848))
$jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$quality = New-Object System.Drawing.Imaging.EncoderParameters 1
$quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([long]85)
$report.Save((Join-Path $root 'src\Maus.Core\Reporting\maus-report-logo.jpg'), $jpeg, $quality)

# Icône : le M aux petites tailles, le logo entier aux grandes.
function Get-DibBytes([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($size * $size * 4 + $maskRow * $size))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bitmap.GetPixel($x, $y)
            $writer.Write([byte]$c.B); $writer.Write([byte]$c.G); $writer.Write([byte]$c.R); $writer.Write([byte]$c.A)
        }
    }
    $writer.Write((New-Object byte[] ($maskRow * $size)))
    $writer.Flush()
    return , $stream.ToArray()
}

$images = @()
$sizes = 16, 24, 32, 48, 64, 128, 256
foreach ($s in $sizes) {
    $frame = if ($s -le 48) { Crop $letter (New-Object System.Drawing.Rectangle 0, 0, $side, $side) $s $s } else { Crop $logo (New-Object System.Drawing.Rectangle 0, 0, 2048, 2048) $s $s }
    if ($s -eq 256) {
        $png = New-Object System.IO.MemoryStream
        $frame.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , $png.ToArray()
    } else {
        $images += , [byte[]](Get-DibBytes $frame)
    }
}

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([int16]1); $writer.Write([int16]32)
    $writer.Write([int]$images[$i].Length); $writer.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'maus.ico'), $out.ToArray())
"images : maus-letter-256.png, maus-logo-512.png, splash.png ($splashW x $splashH), maus-report-logo.jpg, maus.ico"
