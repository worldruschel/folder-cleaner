$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot "..\src\FolderCleaner\Assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$icoPath = Join-Path $outDir "app.ico"

function New-SparklePoints {
    param([float]$cx, [float]$cy, [float]$rOuter, [float]$rInner)
    [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx, $cy - $rOuter),
        [System.Drawing.PointF]::new($cx + $rInner, $cy - $rInner),
        [System.Drawing.PointF]::new($cx + $rOuter, $cy),
        [System.Drawing.PointF]::new($cx + $rInner, $cy + $rInner),
        [System.Drawing.PointF]::new($cx, $cy + $rOuter),
        [System.Drawing.PointF]::new($cx - $rInner, $cy + $rInner),
        [System.Drawing.PointF]::new($cx - $rOuter, $cy),
        [System.Drawing.PointF]::new($cx - $rInner, $cy - $rInner)
    )
}

function Draw-AppIcon {
    param([int]$size)

    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    [float]$s = $size

    # Rounded background badge (modern blue-cyan gradient)
    [float]$pad = $s * 0.05
    $rect = [System.Drawing.RectangleF]::new($pad, $pad, ($s - 2.0 * $pad), ($s - 2.0 * $pad))
    [float]$radius = $s * 0.22
    [float]$d = $radius * 2.0
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $bgBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0),
        [System.Drawing.PointF]::new($s, $s),
        [System.Drawing.Color]::FromArgb(255, 37, 99, 235),
        [System.Drawing.Color]::FromArgb(255, 14, 165, 233)
    )
    $g.FillPath($bgBrush, $path)

    # White stylized folder
    [float]$fx = $s * 0.19
    [float]$fy = $s * 0.29
    [float]$fw = $s * 0.62
    [float]$fh = $s * 0.45
    [float]$tr = $s * 0.06
    [float]$td = $tr * 2.0
    [float]$tabW = $fw * 0.43
    [float]$tabH = $fh * 0.23

    $folderPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $folderPath.AddArc($fx, $fy, $td, $td, 180, 90)
    $folderPath.AddLine($fx + $tr, $fy, $fx + $tabW - $tr, $fy)
    $folderPath.AddLine($fx + $tabW, $fy + $tabH, $fx + $fw - $tr, $fy + $tabH)
    $folderPath.AddArc($fx + $fw - $td, $fy + $tabH, $td, $td, 270, 90)
    $folderPath.AddArc($fx + $fw - $td, $fy + $fh - $td, $td, $td, 0, 90)
    $folderPath.AddArc($fx, $fy + $fh - $td, $td, $td, 90, 90)
    $folderPath.CloseFigure()

    $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(245, 255, 255, 255))
    $g.FillPath($whiteBrush, $folderPath)

    # Subtle accent fold line
    $linePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(55, 37, 99, 235), [Math]::Max(1.0, $s * 0.024))
    $g.DrawLine($linePen, $fx + $s * 0.04, $fy + $tabH + $s * 0.06, $fx + $fw - $s * 0.04, $fy + $tabH + $s * 0.06)

    # Main golden sparkle (cleanliness symbol)
    $sparklePts = New-SparklePoints -cx ($s * 0.72) -cy ($s * 0.67) -rOuter ($s * 0.17) -rInner ($s * 0.048)
    $sparkleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 250, 204, 21))
    $sparklePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 217, 119, 6), [Math]::Max(1.0, $s * 0.015))
    $g.FillPolygon($sparkleBrush, $sparklePts)
    $g.DrawPolygon($sparklePen, $sparklePts)

    # Secondary small sparkle
    if ($size -ge 32) {
        $pts2 = New-SparklePoints -cx ($s * 0.81) -cy ($s * 0.44) -rOuter ($s * 0.08) -rInner ($s * 0.024)
        $g.FillPolygon($sparkleBrush, $pts2)
    }

    $g.Dispose()
    $bgBrush.Dispose()
    $whiteBrush.Dispose()
    $linePen.Dispose()
    $sparkleBrush.Dispose()
    $sparklePen.Dispose()
    $path.Dispose()
    $folderPath.Dispose()

    return $bmp
}

$sizes = @(16, 32, 48, 256)
$pngStreams = @()
foreach ($sz in $sizes) {
    $bmp = Draw-AppIcon -size $sz
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngStreams += ,($sz, $ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($icoPath)
$bw = [System.IO.BinaryWriter]::new($fs)

$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$pngStreams.Count)

$offset = 6 + (16 * $pngStreams.Count)
foreach ($entry in $pngStreams) {
    $sz = $entry[0]
    $bytes = $entry[1]
    $w = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
    $h = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
    $bw.Write($w)
    $bw.Write($h)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}

foreach ($entry in $pngStreams) {
    $bw.Write($entry[1])
}

$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Write-Host "Icon generated successfully: $icoPath"
