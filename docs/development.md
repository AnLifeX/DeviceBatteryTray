# 开发说明

## 环境

- Windows 10 / 11，[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或更新版本
- 主程序：.NET 8，WinForms（任务栏内嵌条、托盘）+ WPF（主页面、弹出面板，使用 [WPF-UI](https://github.com/lepoco/wpfui)）
- 安装程序：.NET Framework 4.8（Win10 / 11 自带，不依赖 .NET 8）+ WPF-UI，界面风格和主程序一致

## 编译与打包

```powershell
pwsh build/package.ps1 -Version 0.1.0
```

输出到 `Releases\`：

| 文件 | 说明 |
|---|---|
| `DeviceBatteryTray-Setup.exe` | 安装包，安装、卸载、自动更新都由它完成 |
| `DeviceBatteryTray-Portable.zip` | 便携版 |

脚本依次：发布主程序（依赖框架，win-x64）→ 生成文件清单 `DeviceBatteryTray.files` → 打成 `payload.zip` →
编译 `src/Setup` 并把 `payload.zip` 嵌进去。安装程序依赖的 WPF-UI 等 dll 也压缩后嵌在 exe 里，所以发布时只有一个文件。

本地版本号在 `src/DeviceBatteryTray/DeviceBatteryTray.csproj` 和 `src/Setup/Setup.csproj` 的 `VersionPrefix`，
打包时都由 `-Version` 覆盖。

## 安装程序（src/Setup）

同一个 exe 有三种模式：

| 启动方式 | 模式 |
|---|---|
| 双击 `DeviceBatteryTray-Setup.exe` | 安装向导：欢迎 → 安装位置 → 附加选项 → 安装 → 完成 |
| 安装目录里的 `Uninstall.exe`（「设置 → 应用」、开始菜单里的卸载都指向它） | 卸载向导：确认 → 卸载 → 完成 |
| `--update --dir <程序目录> --wait-pid <进程> [--background]` | 自动更新：等主程序退出、替换文件、重新启动 |

- 默认装到 `%LOCALAPPDATA%\Programs\DeviceBatteryTray`，在 `HKCU\...\Uninstall\DeviceBatteryTray` 登记，全程不需要管理员权限；
  只有电脑上没有 .NET 8 桌面运行时时，安装程序会从微软官网下载安装，这一步需要管理员确认。
- 替换文件时先解压到 `.setup-new`、旧文件挪到 `.setup-old`，出错回滚；只删除文件清单里登记过的文件。
- 关闭主程序用的是它的退出事件（`Local\DeviceBatteryTray.Exit`），主程序会自己还原任务栏；关不掉时强制结束，再用 `--restore-taskbar` 还原。
- 日志：`%TEMP%\DeviceBatteryTray-Setup.log`。

主程序的自动更新（`Updater.cs`）：查 GitHub 上最新的已发布 Release → 下载 `DeviceBatteryTray-Setup.exe` → 以 `--update` 模式运行它 → 自己退出。
程序目录里有文件清单（安装版、便携版）时才会检查更新；直接运行编译输出不会。

测试更新流程：把便携版解压到任意目录，然后运行

```
DeviceBatteryTray-Setup.exe --update --dir <便携版目录> --background
```

## 发布

CI（`.github/workflows/ci.yml`）只在推送 `vX.Y.Z` 标签时运行：执行打包脚本，并创建**草稿** Release。
确认无误后在 GitHub 上点「Publish release」，已安装的程序才能通过自动更新收到新版本。CI 不上传构建产物、不使用缓存。

```
git tag vX.Y.Z
git push origin vX.Y.Z
```

## 本地测试工具

只在 Debug 编译里有，发布的 Release 版本没有这些入口：

- **演示模式**：设置 →「其他」里打开，或用 `--demo` 启动。用模拟设备展示任务栏电量条、弹出面板和主页面，不读取真实设备；
  低电量提醒照常弹出，「立即更新」会模拟下载（第一次模拟失败，用来展示重试）；重启后自动关闭。
- `--demo-ui [home|settings|flyout|strip] [light|dark] [out.png] [--clean]`：用演示数据打开主页面 / 弹出面板；
  给出 png 路径时渲染成图片后退出。`--clean` 用于生成 README 截图（`docs/images/`）：
  ```
  --demo-ui home light docs/images/main-light.png --clean
  --demo-ui flyout dark docs/images/flyout-dark.png --clean
  --demo-ui strip dark docs/images/taskbar.png --clean
  ```
- 安装程序 `Setup.exe --preview <页面> <out.png>`：把安装程序的某个页面渲染成图片，不执行任何安装操作。页面：
  `welcome` `location` `options` `progress` `error` `done` `confirm` `uninstall-done` `update` `update-error`。

## 程序图标

`app.ico` 由 `tools/IconGen` 生成（方案 ring：深色底 + 渐变电量环 + 闪电）：

```
dotnet run --project tools/IconGen -- src/DeviceBatteryTray/app.ico preview.png --design ring
dotnet run --project tools/IconGen -- --candidates candidates.png   # 所有方案对比
```

`docs/images/logo.png` 是 `app.ico` 里 256px 的那一帧。

## 目录

- `src/DeviceBatteryTray/` 主程序（.NET 8）
  - `Hid/` HID 枚举与请求 / 应答
  - `Devices/` 各设备的电量读取
  - `Tray/` 任务栏内嵌条、主逻辑
  - `Views/` 主页面和弹出面板（WPF：窗口、设备页、设置页、ViewModel、样式）
  - `Ui/` 主题（深浅色）、设备列表、演示数据、程序图标
  - `AppSettings.cs` 设置，`AutoStart.cs` 开机自启，`Updater.cs` 自动更新
- `src/Setup/` 安装程序（.NET Framework 4.8）：安装 / 卸载向导、自动更新
- `build/package.ps1` 打包脚本（本地和 CI 共用）
- `tools/HidProbe/` HID 调试工具：列接口（含 Report ID）、发包、被动监听、读 Feature
- `tools/GameSirProbe/` 32 位，调用盖世小鸡 SDK 并截获它的 HID 收发
- `tools/FlydigiProbe/` 经飞智过滤驱动查询黑武士（需管理员权限）
- `tools/IconGen/` 生成程序图标
- `docs/protocols/` 各设备的协议笔记
