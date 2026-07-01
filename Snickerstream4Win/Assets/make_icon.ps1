Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $pad = [Math]::Round($S * 0.06)
    $rectF = New-Object System.Drawing.RectangleF($pad, $pad, ($S - 2*$pad), ($S - 2*$pad))
    $radius = $S * 0.235

    # rounded-square (squircle-ish) path
    function Get-RoundPath($r, $rad) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $rad * 2
        $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
        $p.AddArc(($r.Right - $d), $r.Y, $d, $d, 270, 90)
        $p.AddArc(($r.Right - $d), ($r.Bottom - $d), $d, $d, 0, 90)
        $p.AddArc($r.X, ($r.Bottom - $d), $d, $d, 90, 90)
        $p.CloseFigure()
        return $p
    }

    $bg = Get-RoundPath $rectF $radius
    $c1 = [System.Drawing.Color]::FromArgb(255, 0x66, 0x38, 0xD9)
    $c2 = [System.Drawing.Color]::FromArgb(255, 0xEB, 0x47, 0x85)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rectF, $c1, $c2, 45.0)
    $g.FillPath($brush, $bg)

    # dual screens (white-ish rounded rects)
    $white = [System.Drawing.Color]::FromArgb(245, 250, 250, 255)
    $screenW = $S * 0.46
    $cx = $S / 2.0
    $topH = $S * 0.20
    $botW = $S * 0.38
    $botH = $S * 0.18
    $gap = $S * 0.045
    $topY = $S * 0.30
    $botY = $topY + $topH + $gap

    $topRect = New-Object System.Drawing.RectangleF(($cx - $screenW/2), $topY, $screenW, $topH)
    $botRect = New-Object System.Drawing.RectangleF(($cx - $botW/2), $botY, $botW, $botH)
    $wb = New-Object System.Drawing.SolidBrush($white)
    $g.FillPath($wb, (Get-RoundPath $topRect ($S*0.03)))
    $g.FillPath($wb, (Get-RoundPath $botRect ($S*0.03)))

    # play triangle on top screen
    $tri = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tcx = $cx
    $tcy = $topY + $topH/2
    $ts = $topH * 0.42
    $pts = @(
        (New-Object System.Drawing.PointF(($tcx - $ts*0.5), ($tcy - $ts*0.6))),
        (New-Object System.Drawing.PointF(($tcx - $ts*0.5), ($tcy + $ts*0.6))),
        (New-Object System.Drawing.PointF(($tcx + $ts*0.75), $tcy))
    )
    $tri.AddPolygon($pts)
    $triBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xF2, 0x4D, 0x73))
    $g.FillPath($triBrush, $tri)

    # wifi arcs on bottom screen
    $blue = [System.Drawing.Color]::FromArgb(255, 0x4D, 0x66, 0xF2)
    $bcx = $cx
    $bcy = $botY + $botH * 0.72
    for ($i = 1; $i -le 3; $i++) {
        $rr = $botH * 0.22 * $i
        $penW = [Math]::Max(1.0, $S * 0.012)
        $pen = New-Object System.Drawing.Pen($blue, $penW)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($pen, ($bcx - $rr), ($bcy - $rr), ($rr*2), ($rr*2), 200, 140)
        $pen.Dispose()
    }
    $dotR = $S * 0.012
    $g.FillEllipse((New-Object System.Drawing.SolidBrush($blue)), ($bcx - $dotR), ($bcy - $dotR), ($dotR*2), ($dotR*2))

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngStreams = @()
foreach ($sz in $sizes) {
    $b = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngStreams += ,($ms.ToArray())
    $b.Dispose()
}

# Assemble ICO container
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)        # reserved
$bw.Write([UInt16]1)        # type icon
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $data = $pngStreams[$i]
    $bw.Write([byte]([Math]::Min($sz,256) % 256))   # width (0 => 256)
    $bw.Write([byte]([Math]::Min($sz,256) % 256))   # height
    $bw.Write([byte]0)      # colors
    $bw.Write([byte]0)      # reserved
    $bw.Write([UInt16]1)    # planes
    $bw.Write([UInt16]32)   # bpp
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngStreams) { $bw.Write($data) }
$bw.Flush()
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
[System.IO.File]::WriteAllBytes((Join-Path $dir 'app.ico'), $out.ToArray())
Write-Host "Wrote app.ico ($($out.Length) bytes)"
