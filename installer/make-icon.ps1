# Renders the app icon: a raised boxing glove in the app's accent red, on transparency.
# Emits a multi-size .ico with PNG payloads (Vista+ reads PNG entries directly).
Add-Type -AssemblyName System.Drawing

$Out = $args[0]
if (-not $Out) { throw "usage: make-icon.ps1 <out.ico>" }

$Glove     = [System.Drawing.Color]::FromArgb(255, 0xE0, 0x41, 0x3F)   # AccentColor
$GloveLit  = [System.Drawing.Color]::FromArgb(255, 0xF0, 0x60, 0x5E)   # AccentHoverColor
$Thumb     = [System.Drawing.Color]::FromArgb(255, 0xB8, 0x32, 0x30)
$Cuff      = [System.Drawing.Color]::FromArgb(255, 0xE6, 0xE8, 0xEC)   # TextColor, reads as the wrist wrap
$CuffShade = [System.Drawing.Color]::FromArgb(255, 0x9A, 0xA0, 0xAA)

function Add-RoundRect {
    param($path, [double]$x, [double]$y, [double]$w, [double]$h, [double]$r)
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2)
    $d = $r * 2
    if ($d -le 0) { $path.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h))); return }
    $path.AddArc($x,          $y,          $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,          $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,          $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()
}

function New-GloveBitmap {
    param([int]$S)
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Normalised layout, y down. Fist on top, thumb to the left, wrap at the wrist.
    $u = { param($v) [double]($v * $S) }

    # Thumb first so the fist overlaps it and the join stays clean.
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundRect $p (& $u 0.07) (& $u 0.38) (& $u 0.30) (& $u 0.31) (& $u 0.15)
    $b = New-Object System.Drawing.SolidBrush($Thumb)
    $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()

    # Fist: tall rounded block, very round at the top so it reads as a glove not a box.
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundRect $p (& $u 0.22) (& $u 0.10) (& $u 0.62) (& $u 0.64) (& $u 0.28)
    $b = New-Object System.Drawing.SolidBrush($Glove)
    $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()

    # Highlight along the top-left of the fist. Dropped below 24px, where it only adds noise.
    if ($S -ge 24) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        Add-RoundRect $p (& $u 0.30) (& $u 0.17) (& $u 0.25) (& $u 0.18) (& $u 0.09)
        $b = New-Object System.Drawing.SolidBrush($GloveLit)
        $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()
    }

    # Knuckle groove, again only where there are pixels to spare.
    if ($S -ge 32) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        Add-RoundRect $p (& $u 0.28) (& $u 0.585) (& $u 0.50) (& $u 0.055) (& $u 0.027)
        $b = New-Object System.Drawing.SolidBrush($Thumb)
        $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()
    }

    # Wrist wrap: the pale band is what separates this from a generic red blob at 16px.
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundRect $p (& $u 0.26) (& $u 0.68) (& $u 0.54) (& $u 0.24) (& $u 0.07)
    $b = New-Object System.Drawing.SolidBrush($Cuff)
    $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()

    if ($S -ge 32) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        Add-RoundRect $p (& $u 0.26) (& $u 0.855) (& $u 0.54) (& $u 0.065) (& $u 0.03)
        $b = New-Object System.Drawing.SolidBrush($CuffShade)
        $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()
    }

    $g.Dispose()
    return $bmp
}

function Get-DibBytes {
    # A classic 32bpp DIB icon entry: BITMAPINFOHEADER with doubled height, BGRA rows bottom-up,
    # then an empty 1bpp AND mask. Older readers (including .NET Framework's Icon class) only
    # understand this form, so the small sizes use it and only 128/256 ship as PNG.
    param($bmp)
    $w = $bmp.Width; $h = $bmp.Height
    $maskStride = [int][Math]::Floor(($w + 31) / 32) * 4
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4 + $maskStride * $h))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = [byte[]]::new($w * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
        $src = [IntPtr]::Add($data.Scan0, $y * $data.Stride)
        [System.Runtime.InteropServices.Marshal]::Copy($src, $row, 0, $row.Length)
        $bw.Write($row, 0, $row.Length)      # LockBits already hands back BGRA
    }
    $bmp.UnlockBits($data)

    $zero = [byte[]]::new($maskStride * $h)
    $bw.Write($zero, 0, $zero.Length)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return $bytes
}

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$blobs = @()
$isPng = @()
foreach ($s in $sizes) {
    $bmp = New-GloveBitmap $s
    if ($s -ge 128) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $blobs += ,($ms.ToArray()); $isPng += $true
        $ms.Dispose()
    } else {
        $blobs += ,(Get-DibBytes $bmp); $isPng += $false
    }
    $bmp.Dispose()
}

# ICONDIR + ICONDIRENTRY[] + payloads
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = $(if ($s -ge 256) { 0 } else { $s })
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$blobs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b, 0, $b.Length) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()
Write-Host ("wrote {0} ({1} bytes, {2} sizes)" -f $Out, (Get-Item $Out).Length, $sizes.Count)
