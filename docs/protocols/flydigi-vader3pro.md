# 飞智 黑武士 3 Pro（Vader3 Pro）电量

> 来源：飞智空间站 `Service/GameController.ServiceHandler.dll`、`LibCommon.dll`（.NET，ilspycmd 反编译）。2026-11-24。

## 难点
接收器在 XInput 模式下伪装成 `045E:028E`（Xbox 360 有线手柄），**没有厂商 HID 接口**。
飞智的私有指令必须经过它的内核过滤驱动 `Xbox360Filter64.sys` + `XboxComm64.dll` 收发；
Windows 自带 XInput 只能读到 “WIRED / FULL”，没有真实电量。

## 手柄原始协议（经过滤驱动）
- 请求：15 字节 `A5 10 00 … CRC`，`0x10` = GamePadInfo，CRC = 前 14 字节之和（低 8 位）。
- 回包 32 字节，`[14]=0xA5`，`[15]=0x10` 时：
  - `[16]` 设备 ID（黑武士 3 Pro = 80）
  - `[17..20]` MAC
  - `[21..22]` 固件版本（BCD 半字节）
  - `[23]` **电压原始值**，电量 = `clamp(v, 98, 114)` 线性映射到 0~100%

## 目前可用的读取方式：问飞智后台服务（本机 UDP）
飞智服务 `FDGMonitorService` 常驻运行，界面程序通过 UDP 跟它通讯：
- 服务监听 `127.0.0.1:7778`，回复发往 `7777`（端口写在 `Service/port.txt`）
- 包格式：`[类型 u16 大端][长度 u16 大端][UTF-8 JSON]`，请求类型 = 3
- 请求 JSON：`{"ActionNew":"GetGamepadInfo"}`
- 回复：`Action = "UpdateGamepadInfo"`，`ActionData` 是 `{报告索引: FDGDeviceInfo}` 的 JSON 字符串，内含 `BatteryPercent`、`IsConnect`、`ShowGameHadleName` 等

实测回复：`ShowGameHadleName = 黑武士3 Pro`，固件 6.7.5.1，`IsConnect = true`，**`BatteryPercent = 0`（待确认，见下）**。

## 结论：服务给的电量不可用
- 手柄一直唤醒、连接中，`BatteryPercent` 仍恒为 0。
- 服务的 `GetGamepadInfo` 只返回连接时缓存的信息，不会重新读手柄。
- 飞智空间站界面代码里**从不显示 BatteryPercent**，这个字段没人维护；对 CH571 新固件（6.7.5.1）的 `[23]` 很可能已不是 98~114 的电压值（例如直接上报百分比，< 98 时被钳成 0）。

## 拿原始字节的障碍
- `XboxComm64.dll` 通过 minifilter 通讯端口 `FilterConnectCommunicationPort` 连 `XboxFilter`，飞智服务以 SYSTEM 身份占着；
  普通权限进程连不上（当前会话非管理员），且这类端口通常只允许一个连接。
- 可选方案（都还没做）：
  1. 以管理员身份、暂停飞智服务后，自己连过滤端口发 `A5 10`，看 `[23]` 的真实含义；
  2. 手柄切到 DInput/蓝牙模式走 HID（会影响游戏里的 XInput 使用）；
  3. 暂不支持这只手柄的电量。

## 实测原始回包（2026-11-24，管理员 + 暂停飞智服务，tools/FlydigiProbe）
```
00 14 00 00 00 00 00 00 00 00 00 00 00 00 A5 10 50 xx xx xx xx 51 67 04 02 00 03 00 00 00 8D 00
                                          │  │  │  └ MAC ──┘  └固件┘ │
                                          │  │  └ 设备ID 80          └ [23] = 0x04
                                          └ 回包头 A5 10
```
- 设备 ID、MAC、固件（6.7.5.1）与飞智服务一致 → 偏移正确。
- `[23] = 0x04`：飞智服务按 98~114 电压换算得 0%，所以一直显示 0。推测是**电量档位**（0~4/5 格），需要充电/放电时再测几个点确认。
- 飞智服务运行时独占过滤驱动的回包回调，我们的进程能发但收不到；停掉服务后才收到。
- 结论：要读黑武士电量，我们的程序需要管理员权限，并且飞智服务不能同时运行（或改为在我们进程里替代它）。

## 与飞智服务共存（2026-11-28 实测）
- 飞智服务运行时：驱动 `IsCurrentUsbCanWrite = True`、`A5 10` 能发出去，但回包全部被服务拿走，我们一个都收不到（连续 8 次 0 命中）。
  所以程序检测到 `GameControllerService` 进程在运行时不碰过滤驱动（已连上的调 `UninitXboxMonitorRes` 断开），
  显示「飞智空间站服务运行中，无法同时读取电量」，不再误报"休眠"。
- `Xbox360Filter64.sys` 按需加载（Start=3），服务没运行时由我们的 `InitXboxMonitorRes` 加载。
- `IsCurrentUsbCanWrite = False` 时（驱动没接管这只手柄）程序显示「飞智驱动未连上手柄」，并每分钟 Uninit + Init 重连一次。
- 手柄醒着时也会偶尔漏一次应答（约每 4~5 次漏 1 次），程序连续 3 次没应答才显示休眠，中间沿用上次读数。
- 遇到过一次飞智空间站自己也连不上、重插接收器 / 重启服务都无效的情况，是飞智手柄服务本身坏了：
  在空间站里按官方操作卸载并重新安装手柄服务、重启空间站后恢复。Steam 等走系统 XInput 驱动（xusb22）的软件不受影响。
