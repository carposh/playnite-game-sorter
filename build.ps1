# Builds GameSorter.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$src = Join-Path $here 'src'
$obj = Join-Path $src 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null

Add-Type -AssemblyName System.Drawing
$ico = Join-Path $obj 'app.ico'
$ink = [System.Drawing.Color]::FromArgb(255, 31, 30, 29)
$cream = [System.Drawing.Color]::FromArgb(255, 240, 238, 230)
$clay = [System.Drawing.Color]::FromArgb(255, 217, 119, 87)
$pngs = foreach ($size in 256, 64, 48, 32, 24, 16) {
    $k = $size / 32.0
    function U([double]$v) { [int][Math]::Round($v * $k) }
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $r = [Math]::Max(2, (U 7)); $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($size - $r - 1, 0, $r, $r, 270, 90)
    $path.AddArc($size - $r - 1, $size - $r - 1, $r, $r, 0, 90); $path.AddArc(0, $size - $r - 1, $r, $r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $ink), $path)
    $g.SmoothingMode = 'None'
    $spines = @(@(5, 14, $cream), @(13, 11, $cream), @(21, 8, $clay))
    foreach ($s in $spines) {
        $x = U $s[0]; $top = U $s[1]; $w = U 6; $bottom = U 24
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $s[2]), $x, $top, $w, $bottom - $top)
        if ($size -ge 48) { $g.FillRectangle((New-Object System.Drawing.SolidBrush $ink), $x + (U 1), $top + (U 2), $w - 2 * (U 1), [Math]::Max(1, (U 1))) }
    }
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $cream), (U 4), (U 25), (U 28) - (U 4), [Math]::Max(1, (U 2)))
    $g.Dispose()
    if ($size -ge 256) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $data = $ms.ToArray() }
    else {
        $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([int]40); $bw.Write([int]$size); $bw.Write([int]($size * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $size - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $size; $x++) { $px = $bmp.GetPixel($x, $y); $bw.Write([byte]$px.B); $bw.Write([byte]$px.G); $bw.Write([byte]$px.R); $bw.Write([byte]$px.A) } }
        $bw.Write((New-Object byte[] ([int]([Math]::Ceiling($size / 32.0) * 4) * $size))); $bw.Flush(); $data = $ms.ToArray()
    }
    if ($size -eq 256) { $bmp.Save((Join-Path $obj 'logo-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , @($size, $data)
}
$fs = [System.IO.File]::Create($ico); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $d = $p[1]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
$w.Close()

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $here 'GameSorter.exe'
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$out" "/win32icon:$ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    "/resource:$(Join-Path $src 'PlayniteExtension\GameSorter.psm1'),GameSorter.psm1" `
    "/resource:$(Join-Path $src 'PlayniteExtension\extension.yaml'),extension.yaml" `
    (Join-Path $src 'GameSorter.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Get-Item $out | Select-Object Name, Length, LastWriteTime
