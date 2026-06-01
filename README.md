# ff14-mobile-act

> 在**只有手机 + PS5**（无 PC、无盒子、无 root）的条件下，把安卓手机当成 FFXIV 的 ACT：
> 解析战斗数据、兼容 PC 版 ACT 生态（cactbot / OverlayPlugin overlay），并直传 FFLogs。

**当前状态：研究 / 概念验证阶段（PoC）。尚未实现，M0 待验证。**

---

## ⚠️ 重要声明（务必先读）

- **仅供个人学习与研究、本地自用。** 请勿公开分发二进制或预编译包。
- 本项目涉及对游戏官方网络流量的中间人（MITM）解密与改写，**违反 Square Enix 服务条款**，使用风险由使用者自行承担，可能导致账号封禁。
- 分发可能触及：闭源 **Oodle Network** 原生库的再分发限制（RAD/Epic）、以及绕过技术保护措施的相关法律（如 DMCA §1201）。本仓库**不提供** Oodle 库，也不授予任何再分发权利。
- 本项目与 Square Enix、FFLogs、RAD Game Tools / Epic **无任何关联**，不受其认可。
- 仓库默认**保留所有权利（无开源许可）**，以避免被理解为授予再分发权。

---

## 它是怎么工作的（机制骨架）

PS5 留在家里 WiFi 上，**只把 DNS 手动指向手机**；手机跑一个用户态代理把整条游戏连接接管过来、解码、再以 ACT 兼容格式输出。**不抓包、不需要 root**——手机是真正的 TCP 端点，内核栈保证零丢包、从首包起，正好满足 FFXIV 有状态 Oodle 压缩的硬约束。

```
PS5 (手动DNS = 手机静态IP)
  │  ① 解析 neolobby0X.ffxiv.com → 手机DNS答自身IP
  ▼
[手机App]
  ├─ DNS 响应器 (bind UDP/53, 只劫持 ffxiv 域名)
  ├─ LobbyProxy : 解 Blowfish → 改写 lobby 下发的 EnterWorld(zone地址) 指向自己
  ├─ ZoneProxy  : 用户态终止式 TCP 中继 (PS5↔手机↔真服务器)
  ├─ 解码       : Oodle 解压 → 去混淆 → opcode 映射
  └─ 输出三件套 :
       • FFXIV_ACT_Plugin 日志行 (Network_*.log) ── 单一事实源
       • OverlayPlugin WebSocket (ws://<手机IP>:10501/ws) → cactbot/overlay 直连
       • FFLogs 文件法上传
  ▼
手机网页 / PC 浏览器 看实时 overlay + 自动传 FFLogs
```

详见 [docs/design.md](docs/design.md)。

---

## 平台

| 平台 | 角色 | 可行性 |
|---|---|---|
| 安卓手机 | DNS + 代理 + 解码 + overlay 服务端 | ✅ 主方案（无 root） |
| iOS | 仅浏览器看板（连安卓机的 WS） | ❌ 不能当服务端（后台不让常驻监听 socket） |
| PS5 | 游戏客户端，仅改 DNS | ✅ |
| NS2(Switch 2) | 同 PS5（封闭、只能串接） | 🔜 上线后按同骨架适配 |

---

## 技术栈 / 上游参考

| 用途 | 上游 | 处置 |
|---|---|---|
| Lobby/Zone 无注入代理 + Brokefish + EnterWorld 改写 | [NotNite/TemporalStasis](https://github.com/NotNite/TemporalStasis) | 移植 |
| Oodle Network TCP 解压 | [ravahn/machina](https://github.com/ravahn/machina) | C# 封装复用；native 换 ARM64 |
| 去混淆（六表） | [perchbirdd/Unscrambler](https://github.com/perchbirdd/Unscrambler) | 移植，每补丁更新 |
| opcode → struct 映射 | [karashiiro/FFXIVOpcodes](https://github.com/karashiiro/FFXIVOpcodes) | 直接用，每补丁更新 |
| ACT 兼容（日志行 + WS） | [marzent/IINACT](https://github.com/marzent/IINACT) · [OverlayPlugin](https://github.com/OverlayPlugin/OverlayPlugin) | 复刻 / 移植 |
| FFLogs 上传 | [MirisWisdom/fflogs.uploader](https://github.com/MirisWisdom/fflogs.uploader) | 移植逻辑（文件法） |

整条链 100% C#/.NET，目标经 **.NET for Android (MAUI)** 在 ARM64 同栈运行。

---

## 路线图

- [ ] **M0 — 生死证明**：手机 DNS + LobbyProxy 让 PS5 真正登入游戏世界；ZoneProxy 中继 + ARM64 Oodle 解压 + 去混淆 → 解出至少一个 `ActionEffect`(type 21)。
- [ ] **M1** — opcode→日志行投影（21/22/26/30/00）+ 写 `Network_*.log` + 手机内 overlay。
- [ ] **M2** — PC 浏览器 cactbot 零改动直连 + 每补丁“金标 trace”校验徽章。
- [ ] **M3** — FFLogs 文件法直传（live-log）。
- [ ] **M4** — 稳定性/续航硬化（前台服务 + WakeLock + 断线/IP 漂移告警）。

## 已知硬限制

- **手机 = 游戏连接命脉**：App 中断 / 息屏 / 来电 → PS5 掉线，且 Oodle 状态报废需回角色选择重登。仅适合“专用机 + 插电 + 屏常亮 + 关省电”的硬核自用。
- **每补丁维护**：去混淆六表 / opcode / EncryptionInit 偏移随补丁变化，补丁日可能静默失效。
- **静默错误**：字段投影若出错，日志行结构仍合法但数值悄悄变错 → 必做金标 trace 校验。
