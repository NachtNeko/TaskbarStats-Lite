<#
.SYNOPSIS
    编译 TaskbarStats —— 只用 Windows 自带的 .NET Framework 编译器。

.DESCRIPTION
    不需要安装 .NET SDK 或 Visual Studio。脚本会自动寻找
    C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe（失败时回退到 32 位目录）。

.PARAMETER Run
    编译完成后立即启动。

.PARAMETER Startup
    编译完成并启动后，把程序加入当前用户的开机启动项（写 .lnk 到启动文件夹）。

.PARAMETER Probe
    编译完成后运行离线自检（--dump），导出真实渲染位图，不开窗口。

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Run -Startup

.EXAMPLE
    .\build.ps1 -Probe
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Startup,
    [switch]$Probe
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

# ---- 定位编译器 ----
$candidates = @(
    Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
) | Where-Object { Test-Path $_ }

if (-not $candidates) {
    throw '找不到 csc.exe。请确认已启用 .NET Framework 4.x（Windows 10/11 默认自带）。'
}
$csc = @($candidates)[0]
$refDir = Split-Path -Parent $csc

$src = Join-Path $repoRoot 'src\TaskbarStats.cs'
$out = Join-Path $repoRoot 'TaskbarStats.exe'

if (-not (Test-Path $src)) { throw "找不到源码：$src" }

Write-Host ("编译器 : {0}" -f $csc) -ForegroundColor DarkGray
Write-Host ("源码   : {0}" -f $src) -ForegroundColor DarkGray

# ---- 编译（注意：系统 csc 只支持 C# 5） ----
& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    "/r:$refDir\System.dll" `
    "/r:$refDir\System.Drawing.dll" `
    "/r:$refDir\System.Windows.Forms.dll" `
    "/out:$out" `
    $src

if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

$kb = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host ("编译成功 : {0}  ({1} KB)" -f $out, $kb) -ForegroundColor Green

# ---- 离线自检 ----
if ($Probe) {
    $base = Join-Path $repoRoot 'canvas'
    # 注意：这是 GUI 子系统程序，PowerShell 的 & 不会等它结束，必须用 -Wait
    Start-Process -FilePath $out -ArgumentList @('--dump', $base) -Wait -NoNewWindow
    Write-Host ("自检输出 : {0}_preview.png / {1}_alpha.png" -f $base, $base) -ForegroundColor Green
}

# ---- 开机自启 ----
if ($Startup) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'TaskbarStats.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $out
    $shortcut.WorkingDirectory = $repoRoot
    $shortcut.Description = '任务栏 CPU / 内存 实时监视'
    $shortcut.Save()
    Write-Host ("已加入开机启动 : {0}" -f $lnk) -ForegroundColor Green
}

# ---- 启动 ----
if ($Run) {
    Get-Process -Name 'TaskbarStats' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Process -FilePath $out
    Write-Host '已启动。退出方式：在任务栏文字上右键 -> 退出' -ForegroundColor Green
}
