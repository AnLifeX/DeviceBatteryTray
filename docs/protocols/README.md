# 设备协议笔记

各设备的电量读取协议，全部来自对官方驱动软件的分析（解包 Electron / 反编译 .NET / 驱动日志 / SDK 调用截获），并在实机上验证过。
下面是摘要，完整的分析过程和原始数据见各设备的笔记。

| 设备 | VID:PID | 读取方式 | 笔记 |
|---|---|---|---|
| VGN V98Pro V2 键盘 | `320F:5088`（2.4G 接收器） | 直接 HID | [vgn-v98pro-v2.md](vgn-v98pro-v2.md) |
| 雷神 ML903 mini 鼠标 | `369B:0114`（星闪接收器）、`369B:00F3`（有线） | 直接 HID | [thunderobot-ml903mini.md](thunderobot-ml903mini.md) |
| 盖世小鸡手柄 | `3537:103E`（2.4G 接收器） | 直接 HID | [gamesir-dongle.md](gamesir-dongle.md) |
| 飞智 黑武士 3 Pro | `045E:028E`（XInput） | 飞智过滤驱动，需管理员权限 | [flydigi-vader3pro.md](flydigi-vader3pro.md) |

## VGN 键盘
- 接口：UsagePage `0xFF1C` / Usage `0x92`，Report ID `0x04`，64 字节
- 请求：`04 <seq_lo> <seq_hi> 1A 00…`（`0x1A` = power_info）
- 应答：`04 <seq> 1A 00 00 00 00 <电量%> <充电>`；命令字为 `FF` 表示键盘休眠
- 来源：ATK V HUB（Electron）`EvisionApi.getPowerInfo`

## 雷神 ML903 mini 鼠标
- 接口：UsagePage `0xFFA0`，输出 Report ID `0xA0`，输入 `0xA1`，64 字节
- 请求：`A0 50 00…`（report_device_status）
- 应答：`A1 50 <在线> … 25 <电量>…`；tag `0x25` 后一字节低 7 位为电量、最高位为充电
- 来源：ML903-NearLink Driver（ITON）自带的通讯日志

## 盖世小鸡手柄
- 接口：`MI_01&COL04`，UsagePage `0xFFA0`，输出 Report ID `0x07`（32 字节），输入 `0x06`（64 字节）
- 请求：`07 03 0A 01 00…`
- 应答：`06 0E 0A 01 | id | fw[2] | mac[6] | 电量%`（接收器会持续推送全 0 的 `06` 包，需过滤）
- 来源：GameSir Connect 的 SDK `sapjoyuniapis_x86.dll`（调用 + IAT hook 截获收发）

## 飞智 黑武士 3 Pro
- 接收器在 XInput 模式下没有厂商 HID 接口，私有指令只能经飞智内核过滤驱动 `Xbox360Filter64.sys`；
  本程序加载飞智安装目录下的 `XboxComm64.dll` 收发（需管理员权限、需已安装飞智空间站，可与飞智后台服务共存）
- 请求：15 字节 `A5 10 00… <和校验>`（GamePadInfo）
- 应答（32 字节）：`[14..15]=A5 10`，`[16]` 设备 ID，`[17..20]` MAC，`[21..22]` 固件，`[23]` 电量
- `[23]` 在 CH571 新固件上推测为 0~4 档（按 ×25% 显示，待更多数据核实）；老固件为 98~114 电压值
- 来源：飞智空间站后台服务（.NET 反编译）
