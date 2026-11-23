# 盖世小鸡手柄（GameSir-Dongle）电量协议

> 来源：GameSir Connect 1.3.7（Electron）调用的官方 SDK `sapjoyuniapis_x86_v1.0.11.dll`。
> 用 32 位探测程序 `tools/GameSirProbe` 直接调用 SDK，并 hook SDK 的 `WriteFile` 拿到原始指令，再用 HidProbe 独立复现（2026-11-24）。

## 设备识别
| 项 | 值 |
|---|---|
| 接收器 | `3537:103E`（`GameSir-Dongle`），同时以 XInput 手柄身份工作 |
| 通讯接口 | `MI_01&COL04`，UsagePage `0xFFA0`，Usage `0x01`，输出 Report ID `0x07`（32 字节），输入 Report ID `0x06`（64 字节） |

## 查询设备信息
发送：`07 03 0A 01 00 …`（补 0 到 32 字节）

回包（接收器在这个接口上会持续推送全 0 的 `06 00 …`，需要跳过，找 `06 0E 0A 01` 开头的包）：
```
06 0E 0A 01 | 03 | 01 06 | xx xx xx xx xx xx | 3C | 00
              id   固件    MAC                  电量  type
```
实测：`3C` → 60%（SDK `GetDeviceInfo` 同时返回 59~60）。

## 备注
- SDK 结构体：`SAPJOYUNI_DEVICEINFO { u8 id; u8 firmware[2]; u8 mac[6]; u8 battery; u8 type; }`。
- 我们的程序直接走 HID，不需要 GameSir Connect 或它的 DLL。
