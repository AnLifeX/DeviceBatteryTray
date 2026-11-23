# 漫步者 HECATE G2 无线版（EDF700074）—— 进行中

> 2026-11-24

## 设备
- 型号 EDF700074 = HECATE G2 三模无线版（杰理 JL7018F6 主控），接收器 `35BB:A209`
- 和官方 HECATE 驱动支持的「G2 无线 7.1 版（C-Media 方案，PID A009）」不是同一产品
- TempoHub、HECATE 1.2.0 的设备列表里都**没有** A209

## 接口
| collection | UsagePage | 报告 |
|---|---|---|
| MI_03 COL01 | 0x000C | 输入 ID 0x01（媒体键） |
| MI_03 COL02 | 0xFF02 | 输入/输出 ID 0x02，64 字节 |
| MI_03 COL03 | 0x000C | 输入/输出/Feature ID 0xED（Feature 16 字节） |

## 已试
- TempoHub EC 协议 `2E/02 AA EC D0 …`、键鼠协议 `02 AA 01 06 …` → 无回复
- HECATE（代理 hidapi.dll 记录）对 THX / G2 MAX 配置发 `D0 35 BB 01 00`、`ED 01` → Report ID 不匹配，无回复
- 被动监听 20 秒 → 无主动上报

## 线索（已排除）
- Feature `0xED`：`ED 0C 01 00 …`。耳机全程在充电，00:27~00:45 共 10 次读取完全不变 → **不是电量**，是固定状态值。

## 现状
- 漫步者没有任何 PC 软件支持这款耳机，没有可参考的实现。
- 剩下只能对 `0xFF02`（Report ID 2，64 字节）盲测指令，有误触发固件升级/恢复出厂等风险，暂未进行。
