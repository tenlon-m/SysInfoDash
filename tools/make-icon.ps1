# 生成 SysInfoDash 的 exe 图标 icon.ico（64x64）
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $root "icon.ico"

$bmp = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # 圆角深色背景
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 14
    $d = $r * 2
    $path.AddArc(2, 2, $d, $d, 180, 90)
    $path.AddArc(64 - 2 - $d, 2, $d, $d, 270, 90)
    $path.AddArc(64 - 2 - $d, 64 - 2 - $d, $d, $d, 0, 90)
    $path.AddArc(2, 64 - 2 - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bgBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(31, 34, 39))
    $g.FillPath($bgBrush, $path)
    $bgBrush.Dispose()
    $path.Dispose()

    # 芯片外框
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(79, 195, 247)), 4
    $g.DrawRectangle($pen, 14, 14, 36, 36)
    $pen.Dispose()

    # 芯片核心
    $chip = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(79, 195, 247))
    $g.FillRectangle($chip, 22, 22, 20, 20)
    $chip.Dispose()

    # 引脚
    $pin = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(129, 199, 132))
    $g.FillRectangle($pin, 12, 18, 4, 6)
    $g.FillRectangle($pin, 48, 18, 4, 6)
    $g.FillRectangle($pin, 12, 40, 4, 6)
    $g.FillRectangle($pin, 48, 40, 4, 6)
    $g.FillRectangle($pin, 18, 12, 6, 4)
    $g.FillRectangle($pin, 40, 12, 6, 4)
    $g.FillRectangle($pin, 18, 48, 6, 4)
    $g.FillRectangle($pin, 40, 48, 6, 4)
    $pin.Dispose()

    $hIcon = $bmp.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($hIcon)
    try {
        $fs = [System.IO.File]::Create($outPath)
        try { $icon.Save($fs) } finally { $fs.Dispose() }
        Write-Output "icon created: $outPath"
    } finally {
        $icon.Dispose()
    }
} finally {
    $g.Dispose()
    $bmp.Dispose()
}
