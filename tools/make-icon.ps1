Add-Type -AssemblyName System.Drawing

function New-SpiderBitmap {
  param([int]$Size)
  $bm = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g  = [System.Drawing.Graphics]::FromImage($bm)
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

  $ink  = [System.Drawing.Color]::FromArgb(255, 18, 20, 26)
  $red  = [System.Drawing.Color]::FromArgb(255, 214, 32, 48)
  $tile = [System.Drawing.Color]::FromArgb(255, 244, 245, 247)
  $edge = [System.Drawing.Color]::FromArgb(255, 203, 207, 214)

  $r = [Math]::Max(2.0, $Size * 0.20); $d = $r * 2
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $d, $d, 180, 90)
  $path.AddArc($Size - $d, 0, $d, $d, 270, 90)
  $path.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
  $path.AddArc(0, $Size - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush $tile), $path)
  if ($Size -ge 32) {
    $g.DrawPath((New-Object System.Drawing.Pen $edge, ([single]([Math]::Max(1.0, $Size*0.012)))), $path)
  }

  $cx = $Size / 2.0
  $cy = $Size / 2.0

  # start x,y  knee x,y  tip x,y  (right side, fractions of Size from centre)
  $legs = @(
    @(0.070,-0.082, 0.230,-0.279, 0.340,-0.107),
    @(0.078,-0.025, 0.287,-0.131, 0.385, 0.033),
    @(0.078, 0.033, 0.287, 0.049, 0.385, 0.180),
    @(0.070, 0.082, 0.246, 0.197, 0.340, 0.344)
  )
  $lw = [single]([Math]::Max(1.35, $Size * 0.050))
  $pen = New-Object System.Drawing.Pen $ink, $lw
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

  foreach ($mirror in -1, 1) {
    foreach ($L in $legs) {
      $p1 = New-Object System.Drawing.PointF (($cx + $mirror*$L[0]*$Size), ($cy + $L[1]*$Size))
      $p2 = New-Object System.Drawing.PointF (($cx + $mirror*$L[2]*$Size), ($cy + $L[3]*$Size))
      $p3 = New-Object System.Drawing.PointF (($cx + $mirror*$L[4]*$Size), ($cy + $L[5]*$Size))
      $g.DrawLines($pen, [System.Drawing.PointF[]]@($p1,$p2,$p3))
    }
  }

  $brush = New-Object System.Drawing.SolidBrush $ink
  $hw = 0.105*$Size; $hh = 0.095*$Size
  $g.FillEllipse($brush, ($cx-$hw), ($cy-0.115*$Size-$hh), (2*$hw), (2*$hh))
  $aw = 0.150*$Size; $ah = 0.195*$Size
  $acy = $cy + 0.115*$Size
  $g.FillEllipse($brush, ($cx-$aw), ($acy-$ah), (2*$aw), (2*$ah))

  $rb = New-Object System.Drawing.SolidBrush $red
  if ($Size -le 20) {
    $dr = [Math]::Max(2.0, $Size*0.16)
    $g.FillEllipse($rb, ($cx-$dr/2), ($acy-$dr/2), $dr, $dr)
  } else {
    $hwid = 0.080*$Size; $hht = 0.100*$Size; $waist = 0.020*$Size
    $hg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $hg.AddPolygon([System.Drawing.PointF[]]@(
      (New-Object System.Drawing.PointF (($cx-$hwid), ($acy-$hht))),
      (New-Object System.Drawing.PointF (($cx+$hwid), ($acy-$hht))),
      (New-Object System.Drawing.PointF (($cx+$waist), $acy)),
      (New-Object System.Drawing.PointF (($cx+$hwid), ($acy+$hht))),
      (New-Object System.Drawing.PointF (($cx-$hwid), ($acy+$hht))),
      (New-Object System.Drawing.PointF (($cx-$waist), $acy))
    ))
    $g.FillPath($rb, $hg)
  }

  if ($Size -ge 48) {
    $er = $Size*0.024
    foreach ($ex in -0.042, 0.042) {
      $g.FillEllipse($rb, ($cx+$ex*$Size-$er), ($cy-0.150*$Size-$er), (2*$er), (2*$er))
    }
  }

  $g.Dispose()
  return $bm
}


$outPath = Join-Path $PSScriptRoot "../src/Spydate.App/Assets/spydate.ico"
$sizes = 16,24,32,48,64,128,256
$frames = @()
foreach ($z in $sizes) {
  $bm = New-SpiderBitmap -Size $z
  $ms = New-Object System.IO.MemoryStream
  $bm.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $frames += ,@{ size=$z; bytes=$ms.ToArray() }
  $bm.Dispose(); $ms.Dispose()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16*$frames.Count
foreach ($f in $frames) {
  $d = if ($f.size -ge 256) { 0 } else { $f.size }
  $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$f.bytes.Length); $w.Write([uint32]$offset)
  $offset += $f.bytes.Length
}
foreach ($f in $frames) { $w.Write($f.bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes($outPath, $out.ToArray())
$w.Dispose(); $out.Dispose()
"wrote $outPath : $((Get-Item $outPath).Length) bytes, $($frames.Count) frames"
