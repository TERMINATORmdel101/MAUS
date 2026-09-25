# Fabrique src/Maus.App/Assets/maus.ico à partir du logo du porteur (assets/logo/maus-logo.jpg), sous Windows.
# Toutes les tailles reprennent le logo entier, sans recadrage, à la demande du porteur. Les tailles 16 à 128
# sont enregistrées en BMP 32 bits :
# le décodeur d'icônes de Windows (WIC, utilisé par WPF) refuse les petites tailles compressées en PNG.
# Seule la taille 256 est en PNG, comme le fait Windows.
#
#   powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1

Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'assets\logo\maus-logo.jpg'
$target = Join-Path $root 'src\Maus.App\Assets\maus.ico'

$logo = [System.Drawing.Bitmap]::FromFile($source)

function Resize([System.Drawing.Bitmap]$image, [int]$size) {
    $result = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($result)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($image, 0, 0, $size, $size)
    $g.Dispose()
    return $result
}

# Entrée BMP d'une icône : BITMAPINFOHEADER, pixels BGRA de bas en haut, puis masque AND vide.
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
    # La virgule empêche PowerShell de dérouler le tableau d'octets.
    return , $stream.ToArray()
}

# Le logo est carré (2048 × 2048) : il est simplement réduit à chaque taille.

$frames = @()
foreach ($s in 16, 24, 32, 48, 64, 128, 256) { $frames += , @($s, (Resize $logo $s)) }


$images = @()
foreach ($frame in $frames) {
    if ($frame[0] -eq 256) {
        $png = New-Object System.IO.MemoryStream
        $frame[1].Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , $png.ToArray()
    } else {
        $images += , [byte[]](Get-DibBytes $frame[1])
    }
}

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
for ($i = 0; $i -lt $frames.Count; $i++) {
    $s = $frames[$i][0]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([int16]1); $writer.Write([int16]32)
    $writer.Write([int]$images[$i].Length); $writer.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
$logo.Dispose()
"Icône écrite : $target ($($out.Length) octets, $($frames.Count) tailles)"
