Add-Type -AssemblyName System.Drawing
function Draw($s) {
  $bmp = New-Object System.Drawing.Bitmap $s,$s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode='AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $r = $s*0.22; $d=$r*2
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc(0,0,$d,$d,180,90); $p.AddArc($s-$d-1,0,$d,$d,270,90); $p.AddArc($s-$d-1,$s-$d-1,$d,$d,0,90); $p.AddArc(0,$s-$d-1,$d,$d,90,90); $p.CloseFigure()
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0,0)),([System.Drawing.Point]::new($s,$s)),([System.Drawing.Color]::FromArgb(255,14,165,233)),([System.Drawing.Color]::FromArgb(255,30,58,138))
  $g.FillPath($br,$p)
  $cx=$s*0.5; $cy=$s*0.66
  $w=[Math]::Max(1.5,$s*0.075)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White),$w
  $pen.StartCap='Round'; $pen.EndCap='Round'
  foreach($rad in @(0.20,0.34,0.48)){ $rr=$s*$rad; $g.DrawArc($pen,$cx-$rr,$cy-$rr,2*$rr,2*$rr,225,90) }
  $dr=$s*0.055; $g.FillEllipse([System.Drawing.Brushes]::White,$cx-$dr,$cy-$dr,2*$dr,2*$dr)
  # yesil onay rozeti
  $bs=$s*0.38; $bx=$s-$bs-$s*0.04; $by=$s-$bs-$s*0.04
  $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,34,197,94))),$bx,$by,$bs,$bs)
  $pw=New-Object System.Drawing.Pen ([System.Drawing.Color]::White),([Math]::Max(1.5,$s*0.06)); $pw.StartCap='Round';$pw.EndCap='Round';$pw.LineJoin='Round'
  $g.DrawLines($pw,@([System.Drawing.PointF]::new($bx+$bs*0.25,$by+$bs*0.52),[System.Drawing.PointF]::new($bx+$bs*0.43,$by+$bs*0.70),[System.Drawing.PointF]::new($bx+$bs*0.75,$by+$bs*0.32)))
  $g.Dispose(); return $bmp
}
$sizes=16,32,48,64,128,256; $imgs=@()
foreach($s in $sizes){ $b=Draw $s; $ms=New-Object System.IO.MemoryStream; $b.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png); $imgs+=,$ms.ToArray(); if($s -eq 256){$b.Save((Join-Path $PSScriptRoot "logo.png"))} }
$fs=[System.IO.File]::Create((Join-Path $PSScriptRoot "app.ico")); $bw=New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0);$bw.Write([uint16]1);$bw.Write([uint16]$sizes.Count)
$off=6+16*$sizes.Count
for($i=0;$i -lt $sizes.Count;$i++){ $s=$sizes[$i]; $wb=if($s -ge 256){0}else{$s}
  $bw.Write([byte]$wb);$bw.Write([byte]$wb);$bw.Write([byte]0);$bw.Write([byte]0);$bw.Write([uint16]1);$bw.Write([uint16]32);$bw.Write([uint32]$imgs[$i].Length);$bw.Write([uint32]$off);$off+=$imgs[$i].Length }
foreach($i in $imgs){$bw.Write($i)}; $bw.Close()
