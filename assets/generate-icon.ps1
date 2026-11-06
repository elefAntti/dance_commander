<#
    Generates DanceCommander.ico from scratch.

    Windows needs a real .ico for the executable icon: <ApplicationIcon> embeds it as a Win32
    resource, and that format takes nothing else. An .ico is a container of several sizes, and the
    small ones are drawn simplified rather than scaled down, because fine detail turns to mush
    below about 32px.

    The mark is the app itself: two panes side by side, the left one carrying the accent bar that
    marks the active pane in the UI.

    Run:  powershell -ExecutionPolicy Bypass -File assets\generate-icon.ps1
#>

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$output = Join-Path $PSScriptRoot 'DanceCommander.ico'

# Palette, kept in step with Theme/Dark.xaml.
$accent = [System.Drawing.Color]::FromArgb(0xFF, 0x7A, 0xAA, 0xFF)
$accentDeep = [System.Drawing.Color]::FromArgb(0xFF, 0x3C, 0x66, 0xC4)
$pane = [System.Drawing.Color]::FromArgb(0xFF, 0x1B, 0x1C, 0x1E)
$paneLight = [System.Drawing.Color]::FromArgb(0xFF, 0x2B, 0x2F, 0x34)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0.5) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h))
        return $path
    }
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $tiny = $size -lt 32

    # Background: a rounded square in the accent, so the mark stays visible on a dark taskbar.
    $radius = if ($tiny) { $s * 0.16 } else { $s * 0.22 }
    $back = New-RoundedPath 0 0 $s $s $radius
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 0),
        (New-Object System.Drawing.PointF $s, $s),
        $accent, $accentDeep)
    $g.FillPath($brush, $back)
    $brush.Dispose(); $back.Dispose()

    # Two panes. Tiny sizes lose the corner radius and the accent bar; anything finer disappears.
    $inset = [Math]::Round($s * 0.17)
    $gap = [Math]::Max(1, [Math]::Round($s * 0.07))
    $top = [Math]::Round($s * 0.21)
    $paneW = ($s - ($inset * 2) - $gap) / 2
    $paneH = $s - $top - $inset
    $paneR = if ($tiny) { 0 } else { $s * 0.05 }

    $dark = New-Object System.Drawing.SolidBrush $pane
    $light = New-Object System.Drawing.SolidBrush $paneLight

    $leftPath = New-RoundedPath $inset $top $paneW $paneH $paneR
    $g.FillPath($light, $leftPath); $leftPath.Dispose()

    $rightPath = New-RoundedPath ($inset + $paneW + $gap) $top $paneW $paneH $paneR
    $g.FillPath($dark, $rightPath); $rightPath.Dispose()

    if (-not $tiny) {
        # The active-pane marker, the same cue the application uses.
        $barH = [Math]::Max(1.0, $s * 0.045)
        $barY = $top - ($barH * 1.8)
        $bar = New-RoundedPath $inset $barY $paneW $barH ($barH / 2)
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xFF, 0xF2, 0xF3, 0xF4))
        $g.FillPath($white, $bar)
        $white.Dispose(); $bar.Dispose()
    }

    $dark.Dispose(); $light.Dispose()
    $g.Dispose()
    return $bmp
}

# Assemble the .ico: ICONDIR, then one ICONDIRENTRY per size, then PNG payloads.
$payloads = @()
foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $payloads += , @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose(); $bmp.Dispose()
}

$file = [System.IO.File]::Create($output)
$writer = New-Object System.IO.BinaryWriter $file

$writer.Write([UInt16]0)                 # reserved
$writer.Write([UInt16]1)                 # type: icon
$writer.Write([UInt16]$payloads.Count)

$offset = 6 + (16 * $payloads.Count)
foreach ($p in $payloads) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }   # 0 means 256
    $writer.Write([Byte]$dim)            # width
    $writer.Write([Byte]$dim)            # height
    $writer.Write([Byte]0)               # palette size
    $writer.Write([Byte]0)               # reserved
    $writer.Write([UInt16]1)             # colour planes
    $writer.Write([UInt16]32)            # bits per pixel
    $writer.Write([UInt32]$p.Bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}

foreach ($p in $payloads) { $writer.Write($p.Bytes) }

$writer.Flush(); $writer.Dispose(); $file.Dispose()

# A large PNG too, for anywhere that wants one (MSIX assets, README, the web).
$large = New-IconBitmap 512
$large.Save((Join-Path $PSScriptRoot 'icon-512.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$large.Dispose()

"wrote $output ($($payloads.Count) sizes: $($sizes -join ', '))"
