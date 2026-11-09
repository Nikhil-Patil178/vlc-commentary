<#
  Builds dist\VlcCommentarySetup.exe (the installer, with the app embedded).
  Uses only the C# compiler that ships with Windows.

  Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw ".NET Framework 4 C# compiler not found" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$icon = Join-Path $root 'assets\app.ico'

# --- icon: orange circle with note lines, stored as PNGs inside an .ico (16-256 px)
if (-not (Test-Path $icon)) {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force (Split-Path $icon) | Out-Null
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 136, 0))), 0.5, 0.5, $s - 1, $s - 1)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, $s * 0.11))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawLine($pen, $s * .28, $s * .34, $s * .72, $s * .34)
        $g.DrawLine($pen, $s * .28, $s * .50, $s * .72, $s * .50)
        $g.DrawLine($pen, $s * .28, $s * .66, $s * .56, $s * .66)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $w.Write($p) }
    [IO.File]::WriteAllBytes($icon, $out.ToArray())
    Write-Host "Created $icon"
}

$refs = '/r:System.Web.Extensions.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
        '/r:System.Core.dll', '/r:Microsoft.CSharp.dll'

# --- the app
& $csc /nologo /target:winexe /optimize+ /win32icon:$icon /out:"$dist\VlcCommentary.exe" $refs "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "App build failed" }

# --- the installer, with the app embedded
& $csc /nologo /target:winexe /optimize+ /win32icon:$icon /main:VlcCommentary.Setup.SetupProgram `
    /resource:"$dist\VlcCommentary.exe,VlcCommentary.exe" `
    /out:"$dist\VlcCommentarySetup.exe" $refs "$root\src\*.cs" "$root\setup\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

Get-Item "$dist\VlcCommentarySetup.exe" | ForEach-Object { Write-Host ("Built {0} ({1:N0} KB)" -f $_.FullName, ($_.Length / 1KB)) }
