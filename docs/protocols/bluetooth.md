# 通用蓝牙设备（系统电量）

> 2026-11-28 · 实现：`src/DeviceBatteryTray/Devices/BluetoothBattery.cs`

不和设备直接通讯，读的是 **Windows 蓝牙栈缓存在设备节点上的电量属性**，也就是「设置 → 蓝牙和其他设备」里显示的那个数。
只调用 SetupAPI，**不需要管理员权限、不需要额外驱动**，一轮读取 2~10 ms。

## 电量从哪来

| 类型 | 系统怎么拿到电量 | 属性写在哪个节点 |
|---|---|---|
| 经典蓝牙（耳机） | 设备通过 HFP 上报（`AT+IPHONEACCEV` / `AT+BIEV`） | HFP 服务子节点 `BTHENUM\{0000111E-…}_…_<地址>_C00000000` |
| BLE（手柄 / 键鼠） | 系统读取并订阅 GATT Battery Service `0x180F` / Battery Level `0x2A19` | 设备节点 `BTHLE\DEV_<地址>` |

两种情况用的是同一个属性：

| 属性 | 键 | 类型 | 用途 |
|---|---|---|---|
| `DEVPKEY_Bluetooth_Battery` | `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` | BYTE | 电量 0~100 |
| （设备是否已连接） | `{83DA6326-97A6-4088-9453-A1923F573B29} 15` | BOOLEAN | 只在设备节点 `BTHENUM\DEV_*` / `BTHLE\DEV_*` 上 |
| `DEVPKEY_Device_FriendlyName` | `{A45C254E-…} 14` | STRING | 显示名（系统里的设备名） |
| `DEVPKEY_DeviceContainer_Category` | `{78C34FC8-104A-4ACA-9EA4-524D52996E57} 90` | STRING_LIST | 设备类型（`Input.Gaming.Gamepad`、`Communication.Headset.Bluetooth`、`Audio.Speakers`…） |
| `DEVPKEY_Bluetooth_ClassOfDevice` | `{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 10` | UINT32 | 经典蓝牙 CoD，类型兜底 |

`{104EA319…} 3` 在 BLE 手柄上也存在（BOOLEAN，一直是 False），含义不明，没有当作充电状态使用；**蓝牙设备暂不显示充电状态**。

断开连接后，属性里还留着最后一次的电量（设备节点本身也可能变成「不在」），所以必须结合「是否已连接」判断。

## 读取流程

1. `SetupDiGetClassDevs(NULL, "BTHENUM" / "BTHLE" / "BTHLEDEVICE" / "BTHHFENUM", DIGCF_ALLCLASSES)`，不带 `DIGCF_PRESENT`（蓝牙关着时节点不在，但属性仍可读）。
2. 从实例 ID 里取 12 位蓝牙地址（先去掉 `{服务 GUID}`，忽略全 0 的本地服务），按地址分组；
   `BTHENUM\DEV_*` / `BTHLE\DEV_*` 是设备本身，其余是它的服务子节点。
3. 组里任一节点有电量属性 → 这台设备支持；一个都没有（音箱、投影仪等）→ **不列出**。
4. 设备节点「已连接」为 True → `Ok`；否则 → `NotFound`（不在主页面占位，重新连上后自动出现）。
5. Key = 蓝牙地址（小写 12 位十六进制），设备 ID 形如 `bluetooth#xxxxxxxxxxxx`，改名 / 隐藏设置按地址记住。

设备类型推断顺序：容器分类 → Class of Device（major `0x04` 音频：minor `0x05`/`0x07` 为音箱，其余为耳机；
major `0x05` 外设：键盘 / 指点设备 / 手柄）→ 子节点（Keyboard / Mouse 类、`XINPUT` 字样、MEDIA 音频）→ 其他。

## 为什么不直接读 GATT

- Win32 `BluetoothGATTGetCharacteristicValue` 或 WinRT `GattDeviceService` 都能读 `0x2A19`，但：
  WinRT 需要把目标框架改成 `net8.0-windows10.0.x`（多带二十多 MB 的投影程序集）；
  Win32 GATT 要打开服务接口句柄，设备没连上时会阻塞或报错。
- 系统已经替我们订阅了 BLE 电量通知并写进属性，读属性和设置里显示的值完全一致，没必要重复读。
- 没有被系统读取电量的 BLE 设备（不带 0x180F 服务，或系统没识别）本程序同样读不到，目前没有遇到这种需要 GATT 兜底的设备。

## 实测（Windows 10 22H2 19045，联发科 RZ616 蓝牙）

| 设备 | 类型 | 电量属性 | 分类 / CoD | 结果 |
|---|---|---|---|---|
| 漫步者 HECATE G2 无线版（蓝牙模式） | 经典蓝牙耳机 | HFP 子节点上 = 100 | `Communication.Headset.Bluetooth` / `0x240404` | 读到 100%（与系统属性一致），识别为耳机 |
| Xbox Wireless Controller | BLE 手柄 | `BTHLE\DEV_*` 上 = 84（上次连接时的缓存） | `Input.Gaming.Gamepad` | 本次未能连上，按未连接处理、不显示；类型识别为手柄 |
| 漫步者 Halo Soundbar | 经典蓝牙音箱 | 无 | `Audio.Speakers` / `0x240414` | 不上报电量，不列出 |
| 投影仪（BLE） | BLE | 无 | `Unknown` | 不列出 |

日志示例：

```
bluetooth#xxxxxxxxxxxx: BatteryReading { State = Ok, Percent = 100, Charging = False, Detail =  } (10ms)
```

## 待验证

- BLE 设备连接时 `{83DA6326…} 15` 是否随之变为 True（本机 Xbox 手柄没连上，未实测；经典蓝牙已确认会变）。
- Windows 11 上同样的属性键（预计一致）。
