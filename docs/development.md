# 开发说明

## 环境

- Windows 10 / 11，[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 主程序：WinForms（任务栏内嵌条、托盘）+ WPF（主页面、弹出面板，使用 [WPF-UI](https://github.com/lepoco/wpfui)）
- 安装、更新：[Velopack](https://velopack.io/)

## 编译与打包

```powershell
dotnet publish src/DeviceBatteryTray -c Release -r win-x64 --self-contained false -o publish
dotnet tool restore   # 安装打包工具 vpk（版本见 .config/dotnet-tools.json）
dotnet vpk pack -u DeviceBatteryTray -v 0.1.0 -p publish -e DeviceBatteryTray.exe --packTitle 设备电量 -i src/DeviceBatteryTray/app.ico -f net8-x64-desktop --splashImage src/DeviceBatteryTray/splash.png --splashProgressColor "#2DD4BF"
```

本地版本号在 `src/DeviceBatteryTray/DeviceBatteryTray.csproj` 的 `VersionPrefix`。

## 发布

CI（`.github/workflows/ci.yml`）只在推送 `vX.Y.Z` 标签时运行：编译、用 Velopack 打包（含相对上一个已发布版本的增量包），
并创建**草稿** Release。确认无误后在 GitHub 上点「Publish release」，已安装的程序才能通过自动更新收到新版本。
CI 不上传构建产物、不使用缓存。

```
git tag vX.Y.Z
git push origin vX.Y.Z
```

测试更新流程：用 `vpk pack` 依次把两个版本打包到同一个目录，安装旧版本后，把环境变量
`DEVICEBATTERYTRAY_UPDATE_DIR` 指向该目录再启动程序，就会从本地目录更新。

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

## 程序图标

`app.ico` 和安装启动画面 `splash.png` 由 `tools/IconGen` 生成（方案 ring：深色底 + 渐变电量环 + 闪电）：

```
dotnet run --project tools/IconGen -- src/DeviceBatteryTray/app.ico preview.png --design ring
dotnet run --project tools/IconGen -- --splash src/DeviceBatteryTray/splash.png --design ring
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
- `tools/HidProbe/` HID 调试工具：列接口（含 Report ID）、发包、被动监听、读 Feature
- `tools/GameSirProbe/` 32 位，调用盖世小鸡 SDK 并截获它的 HID 收发
- `tools/FlydigiProbe/` 经飞智过滤驱动查询黑武士（需管理员权限）
- `tools/IconGen/` 生成程序图标和安装启动画面
- `docs/protocols/` 各设备的协议笔记
