# 打包：发布主程序 → 生成文件清单 → 打成 payload.zip → 编译安装包（把 payload 嵌进去）。
# 输出到 Releases\：
#   DeviceBatteryTray-Setup.exe     安装包（安装 / 卸载 / 自动更新都是它）
#   DeviceBatteryTray-Portable.zip  便携版
#
#   pwsh build/package.ps1 -Version 0.1.0
param(
    [string]$Version = '0.1.0',
    [string]$Out = 'Releases'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root $Out
$work = Join-Path $out '_work'
$app = Join-Path $work 'app'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $app | Out-Null

Write-Host "== 发布主程序 v$Version"
dotnet publish (Join-Path $root 'src/DeviceBatteryTray') -c Release -r win-x64 --self-contained false `
    -p:Version=$Version -p:DebugType=none -o $app
if ($LASTEXITCODE) { throw '发布主程序失败' }

# 文件清单：安装包更新 / 卸载时只删清单里的文件；主程序看到它才会启用自动更新
$manifest = 'DeviceBatteryTray.files'
$files = Get-ChildItem $app -Recurse -File | ForEach-Object { $_.FullName.Substring($app.Length + 1) }
$files + $manifest | Set-Content (Join-Path $app $manifest) -Encoding UTF8

$payload = Join-Path $work 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($app, $payload, 'Optimal', $false)

Write-Host "== 编译安装包"
$setupOut = Join-Path $work 'setup'
dotnet build (Join-Path $root 'src/Setup') -c Release -p:Version=$Version "-p:PayloadZip=$payload" -o $setupOut
if ($LASTEXITCODE) { throw '编译安装包失败' }

Copy-Item (Join-Path $setupOut 'Setup.exe') (Join-Path $out 'DeviceBatteryTray-Setup.exe') -Force
$portable = Join-Path $out 'DeviceBatteryTray-Portable.zip'
if (Test-Path $portable) { Remove-Item $portable }
[IO.Compression.ZipFile]::CreateFromDirectory($app, $portable, 'Optimal', $false)
Remove-Item $work -Recurse -Force

Get-ChildItem $out -File | ForEach-Object { '{0,-36} {1,8:N0} KB' -f $_.Name, ($_.Length / 1KB) }
