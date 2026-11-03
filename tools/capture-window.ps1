Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32Util {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$target = $null
$targetRect = New-Object Win32Util+RECT
$cb = [Win32Util+EnumWindowsProc]{
    param($hwnd, $lParam)
    if (-not [Win32Util]::IsWindowVisible($hwnd)) { return $true }
    $sb = New-Object System.Text.StringBuilder 256
    [Win32Util]::GetWindowText($hwnd, $sb, 256) | Out-Null
    if ($sb.ToString() -eq "SysInfoDash") {
        $script:target = $hwnd
        [Win32Util]::GetWindowRect($hwnd, [ref]$script:targetRect) | Out-Null
        return $false
    }
    return $true
}
[Win32Util]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

if (-not $target) { Write-Output "WINDOW NOT FOUND"; exit 1 }

$w = $targetRect.Right - $targetRect.Left
$h = $targetRect.Bottom - $targetRect.Top
Write-Output "window found at ($($targetRect.Left),$($targetRect.Top)) size ${w}x${h}"

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Win32Util]::PrintWindow($target, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save("D:\MyApp\sysinfo\test-window.png")
$bmp.Dispose()
Write-Output "PrintWindow result: $ok"
