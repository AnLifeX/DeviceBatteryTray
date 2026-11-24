# DeviceBatteryTray

在 Windows 任务栏上显示无线外设电量，**不需要安装各家的驱动软件**。

```
⌨ ▭ 73%    🎮 ▭ 66%
🖱 ▭ 92%    🎮 ▭ 100%
```

协议全部来自对官方驱动的分析（解包 Electron / 反编译 .NET / 驱动日志 / SDK 调用截获），并在实机上验证过。

## 支持的设备

| 设备 | 连接 / VID:PID | 状态 | 读取方式 |
|---|---|---|---|
| **VGN V98Pro V2** 键盘 | 2.4G 接收器 `320F:5088` | ✅ | 直接 HID |
| **雷神 ML903 mini** 鼠标 | 星闪接收器 `369B:0114`、有线 `369B:00F3` | ✅ | 直接 HID |
| **盖世小鸡** 手柄 | 2.4G 接收器 `3537:103E` | ✅ | 直接 HID |
| **飞智 黑武士 3 Pro** | 2.4G 接收器（XInput，`045E:028E`） | ✅ 估算 | 飞智过滤驱动，需管理员 |
| 漫步者 HECATE G2 无线版（EDF700074） | 2.4G 接收器 `35BB:A209` | ❌ 协议未知 | — |

同方案的兄弟型号大概率也能用（未实测）：
- **键盘**：ATK V HUB 里使用同一套 Evision 协议、接收器 PID 同为 `0x5088` 的 VGN / VXE / ATK 键盘，如 VGN S99、VXE75、N75 PRO、V98 PRO、VXE V87、KAFKA 系列等。
- **鼠标**：ITON 方案的雷神鼠标，PID `0113 / 0134 / 0023 / 0024 / 00C1 / 01C4 / 01E3 / 0234`（目前代码只启用了 `0114`、`00F3`）。
- **盖世小鸡**：GameSir Connect 中以 `3537:103E` 作为接收器的型号（T3 Pro、T4N Pro 等）。

## 协议摘要

详细笔记见 [docs/protocols/](docs/protocols/)。

### VGN 键盘 —— [vgn-v98pro-v2.md](docs/protocols/vgn-v98pro-v2.md)
- 接口：UsagePage `0xFF1C` / Usage `0x92`，Report ID `0x04`，64 字节
- 请求：`04 <seq_lo> <seq_hi> 1A 00…`（`0x1A` = power_info）
- 应答：`04 <seq> 1A 00 00 00 00 <电量%> <充电>`；命令字为 `FF` 表示键盘休眠
- 来源：ATK V HUB（Electron）`EvisionApi.getPowerInfo`

### 雷神 ML903 mini 鼠标 —— [thunderobot-ml903mini.md](docs/protocols/thunderobot-ml903mini.md)
- 接口：UsagePage `0xFFA0`，输出 Report ID `0xA0`，输入 `0xA1`，64 字节
- 请求：`A0 50 00…`（report_device_status）
- 应答：`A1 50 <在线> … 25 <电量>…`；tag `0x25` 后一字节低 7 位为电量、最高位为充电
- 来源：ML903-NearLink Driver（ITON）自带的通讯日志

### 盖世小鸡手柄 —— [gamesir-dongle.md](docs/protocols/gamesir-dongle.md)
- 接口：`MI_01&COL04`，UsagePage `0xFFA0`，输出 Report ID `0x07`（32 字节），输入 `0x06`（64 字节）
- 请求：`07 03 0A 01 00…`
- 应答：`06 0E 0A 01 | id | fw[2] | mac[6] | 电量%`（接收器会持续推送全 0 的 `06` 包，需过滤）
- 来源：GameSir Connect 的 SDK `sapjoyuniapis_x86.dll`（调用 + IAT hook 截获收发）

### 飞智 黑武士 3 Pro —— [flydigi-vader3pro.md](docs/protocols/flydigi-vader3pro.md)
- 接收器在 XInput 模式下没有厂商 HID 接口，私有指令只能经飞智内核过滤驱动 `Xbox360Filter64.sys`，
  本程序加载飞智安装目录下的 `XboxComm64.dll` 收发（**需管理员权限**，需已安装飞智空间站，可与飞智后台服务共存）
- 请求：15 字节 `A5 10 00… <和校验>`（GamePadInfo）
- 应答（32 字节）：`[14..15]=A5 10`，`[16]` 设备 ID，`[17..20]` MAC，`[21..22]` 固件，`[23]` 电量
- `[23]` 在 CH571 新固件上推测为 0~4 档（按 ×25% 显示，待更多数据核实）；老固件为 98~114 电压值
- 来源：飞智空间站后台服务（.NET 反编译）

### 漫步者 HECATE G2 无线版 —— [edifier-hecate-g2.md](docs/protocols/edifier-hecate-g2.md)
- 漫步者 TempoHub 和 HECATE 驱动都不支持这款耳机，没有可参考的实现；已排除的尝试记录在笔记里。

## 使用

从 [Releases](https://github.com/AnLifeX/DeviceBatteryTray/releases) 下载 `DeviceBatteryTray.exe` 直接运行（需要 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)）。

- **任务栏电量条**：托盘区左侧显示每个**在线**设备的「设备图标 + 电池格 + 百分比」，多个设备两行排列；
  ≤ 40% 黄色、≤ 20% 红色、充电蓝色。休眠 / 未连接 / 不支持的设备不显示。跟随任务栏深浅色。
- **弹出面板**：点击电量条弹出（Win11 风格，Win10 上同样是圆角 + 阴影）。显示在线设备和更新时间；
  右上角刷新（面板不关闭，原地更新）；有新版本时显示提示；底部「设置」直达主页面的设置页，「退出」。点面板外或按 Esc 关闭。
- **主页面**：双击 exe 打开（程序已在运行时也一样，会唤起已有实例），关闭主页面程序仍在后台运行。
  - 「设备」页：全部支持的设备及状态（在线 / 休眠 / 未检测到接收器 / 需要管理员）。
  - 「设置」页：开机自启、刷新间隔（默认 30 秒）、低电量提醒阈值（默认 20%）、主题（跟随系统 / 浅色 / 深色）、
    读取飞智黑武士开关、自动更新开关和检查间隔、手动检查更新、打开日志。
  - 设置和日志都在 `%LOCALAPPDATA%\DeviceBatteryTray\`（`settings.json`、`DeviceBatteryTray.log`）。
- **自动更新**（可关闭，检查间隔可选 1 小时 ~ 每周，默认 6 小时）：启动 20 秒后及之后按间隔检查 GitHub 最新已发布的 Release
  （需要仓库公开；草稿不算）。程序是便携的单个 exe，没有安装步骤：下载新 exe、校验 SHA-256、替换自己并重启。
  只有单文件发布版支持自我更新。
- **管理员权限**（仅为读取飞智黑武士）：
  - 只有「设置里开着读取黑武士 + 装了飞智空间站 + 插着黑武士接收器」时才请求，否则以普通权限运行、不弹 UAC。
  - 拒绝 UAC 的话照常运行，主页面黑武士卡片上有「以管理员身份重启」按钮；设置里关掉黑武士后永远不提权。
  - 打开「开机自启」后，会创建一个「最高权限」计划任务；之后开机自启和手动双击启动都**不再弹 UAC**。
- 任务栏内嵌的做法与 TrafficMonitor 相同（缩窄任务按钮区、把窗口挂进任务栏），退出、注销、关机时自动还原。目前仅支持 Win10 横向任务栏；竖向任务栏时会改为显示一个托盘图标作为入口。

命令行：
- `DeviceBatteryTray.exe --background`：只常驻任务栏、不打开主页面（开机自启用的就是这个）
- `DeviceBatteryTray.exe --exit`：让正在运行的实例正常退出
- `DeviceBatteryTray.exe --dump out.txt`：读一遍所有设备写出原始收发记录，并用演示数据输出界面预览
  `out.png`（任务栏条 + 面板）、`out-main-dark.png` / `out-main-light.png`（主页面）
- `DeviceBatteryTray.exe --no-elevate`：不请求管理员权限
- 运行日志：`%LOCALAPPDATA%\DeviceBatteryTray\DeviceBatteryTray.log`

## 编译与发布

```powershell
dotnet publish src/DeviceBatteryTray -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

CI（`.github/workflows/ci.yml`）：
- 推送到 main / PR：编译程序和全部调试工具，exe 作为构建产物上传（版本号 `0.x.y-ci.N`）。
- 推送 `vX.Y.Z` 标签：以该版本号编译并创建 GitHub Release，程序的自动更新从这里获取：
  ```
  git tag v0.2.0
  git push origin v0.2.0
  ```
- 本地版本号前缀在 `DeviceBatteryTray.csproj` 的 `VersionPrefix`。

测试更新流程：设置环境变量 `DEVICEBATTERYTRAY_UPDATE_API` 指向一个与 GitHub `releases/latest` 同格式的 JSON，即可用本地服务器模拟发布。

程序图标 `app.ico` 由 `tools/IconGen` 生成：`dotnet run --project tools/IconGen -- src/DeviceBatteryTray/app.ico preview.png`

## 目录

- `src/DeviceBatteryTray/` 托盘程序（.NET 8 WinForms）
  - `Hid/` HID 枚举与请求/应答
  - `Devices/` 各设备的电量读取
  - `Tray/` 任务栏内嵌条、弹出面板、主逻辑
  - `Ui/` 主页面、主题配色与绘制工具、下拉菜单、程序图标
  - `AppSettings.cs` 设置，`AutoStart.cs` 开机自启，`Updater.cs` 自动更新
- `tools/HidProbe/` HID 调试工具：列接口（含 Report ID）/ 发包 / 被动监听 / 读 Feature
- `tools/GameSirProbe/` 32 位，调用盖世小鸡 SDK 并截获其 HID 收发
- `tools/FlydigiProbe/` 经飞智过滤驱动查询黑武士（需管理员）
- `tools/IconGen/` 生成程序图标
- `docs/protocols/` 各设备协议笔记

## 声明

本项目与上述厂商无关。协议通过分析本机已安装的官方软件得到，仅用于读取自己设备的电量。
