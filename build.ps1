# Builds GameSorter.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$src = Join-Path $here 'src'
$obj = Join-Path $src 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null

# App icon: a game-controller glyph on a rounded tile, packed as a multi-size .ico.
Add-Type -AssemblyName System.Drawing
$ico = Join-Path $obj 'app.ico'
$pngs = foreach ($size in 256, 48, 32, 16) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $r = [int]($size * 0.22); $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($size - $r - 1, 0, $r, $r, 270, 90)
    $path.AddArc($size - $r - 1, $size - $r - 1, $r, $r, 0, 90); $path.AddArc(0, $size - $r - 1, $r, $r, 90, 90); $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $size, $size), ([System.Drawing.Color]::FromArgb(255, 124, 77, 255)), ([System.Drawing.Color]::FromArgb(255, 32, 156, 238))
    $g.FillPath($brush, $path)
    $font = New-Object System.Drawing.Font 'Segoe MDL2 Assets', ($size * 0.5), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString([string][char]0xE7FC, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($size * 0.04), $size, $size), $fmt)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , @($size, $ms.ToArray())
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
