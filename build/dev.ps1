<#
开发模式：修改代码或 XAML 后自动重新编译并重新打开窗口（dotnet watch），不需要安装。

  pwsh build/dev.ps1                    主页面
  pwsh build/dev.ps1 settings dark      设置页，深色
  pwsh build/dev.ps1 empty              没有设备时的主页面
  pwsh build/dev.ps1 flyout             弹出面板
  pwsh build/dev.ps1 welcome -Setup     安装程序的页面：welcome location options progress error done
                                        confirm uninstall-done update update-error

主程序使用演示数据：不读取设备、不嵌入任务栏、不写入 settings.json，与已安装的版本互不影响。
关闭窗口后，下次修改时重新打开。Ctrl+C 退出。
#>
param(
    [string]$Page = 'home',
    [ValidateSet('light', 'dark', 'system')][string]$Theme = 'system',
    [switch]$Setup
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# 每次修改都重新启动：界面在窗口创建时就已生成，热重载改不到已经显示的内容
if ($Setup) {
    dotnet watch --project "$root/src/Setup" --no-hot-reload run -- --preview $Page
} else {
    dotnet watch --project "$root/src/DeviceBatteryTray" --no-hot-reload run -- --demo-ui $Page $Theme
}
