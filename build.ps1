# 一键编译 SysInfoDash（使用系统自带的 .NET Framework C# 编译器，无需安装任何 SDK）
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $csc)) {
    Write-Error "未找到 csc.exe：$csc"
    exit 1
}

$icon = Join-Path $root "icon.ico"
if (-not (Test-Path -LiteralPath $icon)) {
    Write-Host "未找到 icon.ico，先运行 tools/make-icon.ps1 生成..."
    & (Join-Path $root "tools\make-icon.ps1")
}

$sources = Get-ChildItem -LiteralPath (Join-Path $root "src") -Filter *.cs |
    Sort-Object Name | ForEach-Object { $_.FullName }

$out = Join-Path $root "SysInfoDash.exe"

$cscArgs = @(
    "/nologo",
    "/target:winexe",
    "/optimize+",
    "/codepage:65001",
    "/win32manifest:$(Join-Path $root 'app.manifest')",
    "/win32icon:$icon",
    "/out:$out",
    "/r:System.dll",
    "/r:System.Core.dll",
    "/r:System.Windows.Forms.dll",
    "/r:System.Drawing.dll",
    "/r:System.Management.dll",
    "/r:System.Web.Extensions.dll"
) + $sources

Write-Host "编译中..."
& $csc $cscArgs

if ($LASTEXITCODE -eq 0) {
    $size = (Get-Item -LiteralPath $out).Length
    Write-Host "编译成功：$out ($([math]::Round($size / 1024)) KB)"
} else {
    Write-Host "编译失败，退出码：$LASTEXITCODE"
    exit 1
}
