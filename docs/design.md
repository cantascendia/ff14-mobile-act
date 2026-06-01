# FF14 纯手机主机端 ACT 方案书（PS5 + 安卓手机，无 PC / 无盒子 / 无 root）

> 场景固定：玩家只有【手机 + PS5】，家里有 WiFi。要把手机当成 PS5 的 ACT，并兼容 PC 版 ACT 生态（cactbot / OverlayPlugin overlay）、能直传 FFLogs。
> 本文区分【已证实】(有来源) 与【推测/需自测】，不和稀泥。撰写：2026-06-01。

---

## 1. 生死裁决

**裁决：conditional（强偏 enabling，卡点在工程落地与运营，不在机制）。**

机制层这条路是**活的**。"纯手机 DNS 重定向 + 用户态终止式 TCP 中继"六个关键问题逐条被钉死，全朝有利方向（均为已证实）：

1. PS5 支持手动指定主/备 DNS，且**不支持加密 DNS(DoH/DoT)/VPN app**，系统级名字解析必走你的手机 DNS。
2. lobby 靠硬编码**域名** `neolobby0X.ffxiv.com`（按数据中心映射，Aether=neolobby02 等），可被自建 DNS 重定向到手机。
3. zone/world 地址是 lobby 在登录响应里**下发的 IP**（非二次 DNS），所以 DNS 改不了 zone，必须由代理**改写 lobby 下发的 EnterWorld 包**——这正是 TemporalStasis `OverwriteEnterWorld()` 的做法。
4. TemporalStasis 官方就支持"自建 DNS 覆盖 neolobby 记录"作为重定向方式，且 lobby→zone 改写全在代理内部完成，**不需要改主机 hosts/启动参数**——完美契合 PS5 限制。
5. "要改 zone 必须先解 lobby"的前提**已满足**：TemporalStasis 自带 Brokefish 实现，密钥从握手明文 `EncryptionInit` 段经 MD5 派生（种子明文上线，MITM 可算），解密→改写→重加密链路完整。
6. lobby/zone 是**明文 TCP**（仅 lobby 一层 Blowfish，zone 只压缩/混淆），零售客户端**无 TLS / 无 cert pinning / 无反 MITM 自校验**，无注入 inline 代理已在零售客户端验证可用。

终止式 TCP 中继让手机成为**真正的 TCP 端点**，内核栈保证从 SYN 后首字节起、按序、零丢包——这恰好满足 FF14 有状态 Oodle Network TCP "必须从首包起完整重建"的硬约束，原理上严格优于 pcap 旁观抓包。被动抓包路（VpnService 看不到 tethered 流量）对纯手机确实死；本路是纯手机方案**唯一可行的技术骨架**。

### 之所以是 conditional 而非纯 enabling，必要条件（缺一不可）

- **(C1) 安卓平台**：iOS 原理性不可行（见 §4）。本方案 = 安卓机方案。
- **(C2) TemporalStasis 移植到 .NET on Android**：C# 逻辑可复用，但 Oodle Network 是闭源 native，PC 是 x64 DLL，**必须替换为 ARM64 版并重做 P/Invoke**（唯一非 C# 硬阻塞）。
- **(C3) 手机做专用准固定设备**：静态 IP / DHCP 保留、插电、屏常亮、关省电、置散热处。命脉串在手机上（见 §8）。
- **(C4) 每补丁维护三处**：去混淆六表(Unscrambler) + opcode 映射(FFXIVOpcodes) + EncryptionInit 偏移/KeyVersion。
- **(C5) 实现期两验证点**：目标 ROM 是否放行普通进程 `bind UDP/53`；ARM64 Oodle native 库的获取与对齐。
- **(C6) 无 PS5 实测公开先例**：协议跨平台一致是**推断**，需自测确认（唯一 medium 置信度的机制环节）。

> 一句话：机制层成立 ≠ 普通玩家用得下去。这是一条"能造出来"的路，但代价全压在运营纪律、维护供应链和单点稳定性上。

---

## 2. 架构：数据流文字图

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ PS5 (零售客户端, 无TLS/无cert-pinning, 主DNS手动 = 手机静态IP 192.168.x.x)    │
└───────────────┬───────────────────────────────────────────────┬─────────────┘
                │                                                 │
   ① DNS查询 neolobby0X.ffxiv.com (明文UDP/53)          ⑤ TCP连到"伪zone地址"
                │  (PS5不支持DoH/DoT,必走手机DNS)        (= 手机ZoneProxy.PublicEndpoint)
                ▼                                                 ▼
┌─────────────────────────┐                       ┌──────────────────────────────┐
│ [A] 手机 DNS 响应器       │                       │ [C] 手机 ZoneProxy(用户态中继) │
│  bind UDP/53             │                       │  · accept PS5 TCP            │
│  neolobby0X → 返回手机IP  │                       │  · SetNextServer(真实zoneIP) │
│  其它域名 → 透传上游DNS    │                       │  · 终止式代理:手机=TCP端点     │
└───────────┬─────────────┘                       │    内核栈保证零丢包/按序/全字节│
            │ PS5据此连手机当lobby                   └───────────────┬──────────────┘
            ▼                                                       │ 明文zone字节流(双向中继)
┌──────────────────────────────────────────┐                      │ 同步tee一份给解码器
│ [B] 手机 LobbyProxy (TemporalStasis移植)    │                      ▼
│  ② accept PS5 → 连真实lobby(neolobby真IP)   │      ┌────────────────────────────────────┐
│  ③ Brokefish解密(key=MD5(EncryptionInit     │      │ [D] 解码流水线 (每包, 有状态)         │
│     明文段+0x12345678+KeyVersion))          │      │  ⑥ Oodle Network TCP 解压(有状态,     │
│  ④ 拦 EnterWorldOpcode(Clientbound):        │      │     必须从首包起,否则永久错位)         │
│     原始 zoneIP:port  ──┐                   │      │  ⑦ 去混淆(Unscrambler 六表)           │
│     改写为手机ZoneProxy.PublicEndpoint        │      │  ⑧ opcode → struct 映射(FFXIVOpcodes) │
│     再 EncipherPadded 重新加密回发PS5        │      └───────────────┬────────────────────┘
│     zoneProxy.SetNextServer(原始zoneIP:port)─┘                      │ 解码出的游戏消息
└──────────────────────────────────────────┘                      ▼
                                                    ┌────────────────────────────────────┐
                                                    │ [E] 战斗事件层 (复刻FFXIV_ACT_Plugin) │
                                                    │  opcode→LogLineType + 字段投影 + hash │
                                                    │  → 唯一事实源: Network_<ts>.log 日志行 │
                                                    └───┬───────────────┬──────────────┬──┘
                                                        │(a)            │(b)           │(c)
                              ┌─────────────────────────┘               ▼              └──────────────┐
                              ▼                          ┌──────────────────────────────┐             ▼
              ┌───────────────────────────┐              │ OverlayPlugin WebSocket Server │  ┌───────────────────────────┐
              │ ACT网络日志行 (文件,UTF-8追写) │            │ ws://<手机IP>:10501/ws          │  │ FFLogs 上传                │
              │ type|ISO8601±off|字段..|hash │             │ subscribe/callOverlayHandler   │  │ 读 Network_*.log →官方parser→│
              │ 21 NetworkAbility/26 Buff... │ ──────────► │ {type:broadcast,msgtype,msg}   │  │ 私有 /client/* 链           │
              │ (= FFLogs & overlay 数据源)  │  喂事件      │ → cactbot/浏览器overlay 直连    │  │ (login→create-report→...)   │
              └────────────┬──────────────┘              └──────────────┬───────────────┘  └──────────────┬────────────┘
                           └────────────────────────────────────────────┴──────── 同一台手机内进程 ─────────┘
```

**关键不变式**：DNS 只解决 lobby 第一跳；zone 必须靠 `[B]` 改写包劫持 → `[C]` 接管。`[C]` 是终止式代理（手机=真 TCP 端点），不是旁观 pcap。`[C]→[D]` 是命脉与解码流的 tee：100% 字节转发回 PS5，同时复制一份进解码。`[E]` 产出的 `Network_*.log` 日志行是 overlay 与 FFLogs 共用的**单一事实源**。

---

## 3. 兼容 PC 版 ACT 的具体实现

蓝本：**完整克隆 IINACT 架构，不发明新协议**。IINACT 已证明可在非 ACT 宿主里跑 FFXIV_ACT_Plugin、输出标准日志行、用 WebSocket 兼容 OverlayPlugin 协议与 FFLogs。

### 3.1 输出 FFXIV_ACT_Plugin 日志行

格式是管道分隔的 "network log line"：`十进制LogLineType | ISO-8601时间戳±offset | 字段… | 行尾hash`。
示例（type 00）：`00|2021-04-26T14:12:30.0000000-04:00|0839||You change to warrior.|d8c450105ea12854e26eb687579564df`

- 这些行**不是原始包**，是 FFXIV_ACT_Plugin 解析/过滤后产出的。手机端要**复刻插件的 `opcode→LogLineType` 映射 + 字段投影 + 逐行 hash + ISO 时间戳格式化**，这是兼容层最大的单点重写工作量。
- 战斗核心行类型：`21(0x15) NetworkAbility`、`22(0x16) NetworkAOEAbility`、`26(0x1A) NetworkBuff`、`30(0x1E) NetworkBuffRemove`、`03/04` 增删 combatant、`19` death、`00` LogLine。
- type 21 字段序：0 type/1 caster ID/2 caster name/3 ability ID/4 ability name/5 target ID/6 target name/7–22 action-effect 对(flags+damage)/23-24 target HP/maxHP/25-26 MP/29-31 XYZ/38-40 caster XYZ/41 heading/42 sequence ID/…/行尾 hash。
- **逐行 hash 是手机对自己输出算的**，只证明"该行未被篡改/截断"，**不证明语义正确**（见 §8）。

### 3.2 复刻 OverlayPlugin WebSocket（让现有 cactbot/overlay 直接连）

| 项 | 做法 |
|---|---|
| 端点 | 手机起 OverlayPlugin 兼容 WS 服务端，监听 `ws://<手机静态IP>:10501/ws`。**不要绑 127.0.0.1**——要让局域网 PC/平板浏览器可达。 |
| overlay 接入 | 浏览器里 cactbot/overlay 的 URL 加 `?OVERLAY_WS=ws://<手机IP>:10501/ws`，**overlay 侧零改动**。 |
| 必实现客户端 API | `addOverlayListener`/`removeOverlayListener`/`startOverlayEvents`/`callOverlayHandler({call:...})`(请求-响应：`getLanguage`/`getCombatants`/`saveData`/`loadData`/`say`/`broadcast` + cactbot 专用 handler)。 |
| 服务端推送帧 | `{"type":"broadcast","msgtype":"CombatData","msg":{...}}`(DPS/HPS 聚合)；`LogLine`/`ChangeZone`/`ChangePrimaryPlayer`/`OnlineStatusChanged`/`Chat`。 |
| 双协议 | 同时支持 modern(`/ws` JSON) 与 legacy WS，最大化兼容面。 |
| handler 来源 | 文档不全 → 从 `ngld/OverlayPlugin` 的 `WSServer.cs` 与 `marzent/IINACT` 源码逐一抠。 |

### 3.3 FFLogs 直传

FFLogs **无公开上传 API**（v1/v2 GraphQL 只读）。上传只能走官方 Uploader 的**私有 `/client/*` 协议**，且它在客户端用**闭源 parser** 把 ACT 日志行解析成 fights。

| 路线 | 内容 | 取舍 |
|---|---|---|
| **(A) 文件法（推荐/低风险）** | 手机先写标准 `Network_<ts>.log`（IINACT 做法，宣称"100% FFLogs 兼容"），再交给**移植版** Uploader（照搬开源 `MirisWisdom/fflogs.uploader` 的 main.js）：`POST /client/login/`(version/email/password,非OAuth) → `create-report`(得 reportCode) → 打包 master-table → `POST /client/set-master-table/` → `POST /client/add-to-log/` → `POST /client/terminate-log/{reportCode}`。支持 live-log。 | 依赖闭源 parser 但与官方一致、最稳；解析在 Uploader 端，自己不碰。 |
| (B) 协议法（不推荐） | 手机原生重实现整条私有 `/client/*` + 自写 fight parser。 | 未文档化、跨 parserVersion 易碎、ToS 敏感、高风险。 |

> overlay WS 与 FFLogs 文件法**共享同一上游** `Network_*.log`，这是兼容层的单一事实源。

---

## 4. 平台矩阵

| 维度 | 安卓（主推） | iOS（妥协/否决） |
|---|---|---|
| 当 PS5 的 DNS+中继服务端 | **可行**：普通 socket 服务端，无需 root/VpnService/pcap | **原理性不可行**：后台监听 socket，NWListener ~15 分钟即停 |
| 长时常驻 | 前台服务 `FOREGROUND_SERVICE_CONNECTED_DEVICE`（语义吻合，不在 Android 15 时限名单） | 无等价机制 |
| CPU 不休眠 | 必持 `PARTIAL_WAKE_LOCK` + `WifiLock`(低延迟/禁省电) | — |
| 网络扩展替代路 | 不需要 | `NEDNSProxyProvider` 仅限 supervised(MDM) 设备；NE 只管 iPhone 自身流量，**管不到 PS5** |
| 分发 | 自签 APK 直装 | App Store 必拒；侧载解决不了后台监听阻断 |
| 特权端口 | bind UDP/53 需实测 ROM 是否放行非特权进程(多数放行) | — |
| **裁决** | **可行（有条件）** | **放弃当服务端** |

> **iOS 唯一妥协路径**：iPhone 仅能当**纯浏览器看板**——连一台安卓机已起好的 `ws://<安卓IP>:10501/ws` 看 overlay。iPhone 不能当 DNS/中继服务端。

---

## 5. 用户 UX

### 5.1 一次性配置（首次，约 5 分钟，门槛在此）

1. **手机固定 LAN IP**：路由器 DHCP 保留（按 MAC，推荐）或手机设静态 IP。**这是命脉**——否则 IP 漂移会断链。失败模式是"连不上"(可诊断)，不是隐性数据错误。
2. **PS5 改 DNS**：设置 → 网络 → 设置网络连接 → 高级 → DNS 设为 Manual → 主 DNS = 手机固定 IP。
3. **手机省电豁免**：关电池优化、允许前台常驻、禁 WiFi 省电。
4. **FFLogs 账号**：App 内填 FFLogs 邮箱/密码（供私有 `/client/login`）。
5. **overlay 配置**（看 overlay 才需）：浏览器开 cactbot，URL 加 `?OVERLAY_WS=ws://<手机IP>:10501/ws`，存书签。

> 静态 IP / DHCP 保留对不懂网络的玩家是真门槛，但可用 App 内向导（自动探测 LAN IP、生成 PS5 逐步图文、检测 IP 漂移红色告警）降低，且属一次性。

### 5.2 日常（每次开打）

```
开App  →  确认状态灯"DNS✓ / 代理监听✓ / 静态IP✓ / 已插电"
   │
   ▼
进游戏(PS5)  →  必须在【角色选择画面或之前】App 已在监听
              (Oodle 有状态:必须截到登录会话首包,否则整局无法解码)
   │
   ▼
看 overlay  →  (i) 手机 App 内嵌 WebView  或  (ii) PC/平板浏览器开书签
   │
   ▼
打完副本  →  App 自动 tail Network_*.log → 自动上传 FFLogs(live-log 或收尾) → 弹报告链接
```

**日常零操作的前提是一次性配置正确 + 设备纪律到位。** 因连接=命脉，App 必须常驻通知 + 醒目状态灯，实时监测息屏/锁屏/来电/切后台/过热降频（任一可拉断 PS5 连接、Oodle 状态报废需回角色选择画面重登）。理想 LAN 额外延迟个位数毫秒，但手机 WiFi 省电/信号是真正 ping 瓶颈 → 接强 5GHz、禁省电、插电常亮。

---

## 6. 技术栈复用清单

整条链 100% C#/.NET，可经 **.NET for Android（原 Xamarin，已并入 .NET 8/MAUI）** 在 ARM64 同栈运行。

| 组件 | 上游 / 维护者 | 处置 | 移植要点 / 风险 |
|---|---|---|---|
| Lobby/Zone inline 代理 | **TemporalStasis** (NotNite) | **移植** | 100% C#，无注入；Brokefish 解密 + EnterWorld 改写直接用。需跑在 .NET on Android |
| Oodle Network TCP 解压 | **Machina** (ravahn) 封装外部 native | C# 封装**复用**；native **必须换** | **唯一硬移植点**：PC 是 x64 DLL，安卓需 **ARM64 版 Oodle Network native 库** + 重做 P/Invoke |
| 去混淆（六表） | **Unscrambler** (perchbirdd) | **移植** | **每补丁更新六表** |
| opcode → struct/类型 | **FFXIVOpcodes** (karashiiro) | **直接用**（数据，按补丁取） | **每补丁更新映射** |
| 网络日志行生成 | **FFXIV_ACT_Plugin** 行为 (ravahn) | **复刻**（最大重写量） | opcode→LogLineType + 字段投影 + 逐行 hash + ISO8601±offset |
| Overlay WS 服务端 | **IINACT**(marzent) / **ngld OverlayPlugin** | **移植**（照搬协议） | modern+legacy 双协议；handler 从源码抠 |
| 日志文件写出 | IINACT 模式 | **新写**（薄） | UTF-8 追写 `Network_<ts>.log` |
| FFLogs 上传 | **MirisWisdom/fflogs.uploader** | **移植逻辑**（重写为 C#） | 私有 `/client/*`；文件法；**最脆弱、ToS 敏感** |
| DNS 响应器 | 自写 | **新写**（极薄） | bind UDP/53，neolobby0X→自身 IP，其余透传 |

**维护者现实**：六表/opcode 的持续逆向产出依赖社区上游，巴士因子低；EncryptionInit 偏移/KeyVersion 需自维护。每补丁这三处不更新，方案在补丁日**静默坏掉**。

---

## 7. MVP 里程碑

**M0（生死证明，最高优先）——证明纯手机能让 PS5 登入并解出一个 ActionEffect。**
- 手机起极简 DNS 响应器(neolobby0X→自身 IP) + LobbyProxy(TemporalStasis 移植，Brokefish 解密 + 改写 EnterWorld)。
- PS5 手动 DNS 指向手机 → **PS5 能正常登入游戏世界**（证明链在 PS5 零售端跑通，消解 C6 无先例风险）。
- ZoneProxy 终止式中继 + Oodle 解压(ARM64 native) + Unscrambler 去混淆 → **解出至少一个 `ActionEffect`(type 21)** 并打印 caster/ability/target/damage。
- **M0 通过 = 整条机制骨架在真实 PS5 上被证实**，全项目命门。最可能失败点：(a) ROM 不放行 bind 53；(b) ARM64 Oodle 库对不齐。

**M1 —— 日志行 + 本地 overlay。** 复刻 `opcode→LogLineType` 投影(先 21/22/26/30/00) + 逐行 hash + 写 `Network_*.log`；起 WS(`:10501/ws`)，手机 WebView cactbot 显示实时 DPS。

**M2 —— PC overlay 零改动直连 + 金标校验。** WS 绑手机 IP，PC 浏览器 cactbot 连上；引入"每补丁金标 trace 比对"(见 §8)，出"parse verified/UNVERIFIED"徽章。

**M3 —— FFLogs 文件法直传。** 移植 Uploader 跑 `/client/*`；live-log tail；断流遭遇打"partial/do-not-rank"不上传。

**M4 —— 稳定性/续航硬化。** connectedDevice 前台服务 + WakeLock + WifiLock + 断线告警 + IP 漂移检测；过热/息屏/来电演练。

---

## 8. 诚实风险

**(R1) 手机=连接命脉【拓扑硬伤，不可缓解只可降频】。** 手机进程任何中断 → PS5↔手机 TCP 被 RST/超时 → PS5 掉线；Oodle 有状态导致解码**永久错位、必须退回角色选择画面**。零式/绝本里一次手机抖动 = 团灭/重开。这把娱乐外设焊成了游戏主干道上的单点故障基础设施。

**(R2) 设备用途冲突【反人类约束】。** 几小时内不接电话、不息屏、不切后台、插电捂散热。一次来电=一次实战事故。缓解：专用机 + 飞行模式只留 WiFi + 关通知 + 插电 + 屏常亮 + 散热垫。**因此本方案不适合普通玩家，只适合愿献祭一台专用机的硬核玩家。**

**(R3) 延迟与续航。** 理想 LAN 额外延迟个位数毫秒；真正 ping 瓶颈是手机 WiFi 省电/信号，WifiLock 低延迟 + 强 5GHz + 禁省电可压。插电+不捂着整场没问题，纯电池长时明显发热掉电。

**(R4) rDPS 只能估算？——纠正普遍误解。** rDPS/aDPS/nDPS 是 **FFLogs 上传后服务端算的**，ACT 本身从不算 rDPS。所以 rDPS **不是手机缺的数据**，只要日志行字段正确，任何上传者(PC/手机)拿到的 rDPS 完全一致。**真正的风险方向相反**：手机从零复刻字段投影，一旦 buff/debuff 字段错位或漏投，rDPS 会**静默变错**(不报错)，与正确上传无法区分。诚实分级：
  - **Tier 1（硬通货）**：自己角色的 ability/heal/cast 原始事件 + 自身时间线。
  - **Tier 2（FFLogs 服务端派生，与 PC 同等）**：rDPS/aDPS/nDPS/parse%，**前提是字段投影正确**。
  - **Tier 3（单客户端重建受限）**：他人 DPS/uptime/有效 HPS——受"单客户端只看到自己客户端被告知的内容"上限，**与 PC ACT 同顶**，非手机独有缺陷。

**(R5) 静默错误无护栏【数据真相 killer】。** 单个字段投影错产出的日志行**结构合法、通过逐行 hash、干净上传、得到貌似合理但错误的数字**。无参考客户端交叉校验。**必做缓解**：每补丁随包一个 PS5 金标 trace，diff 手机投影 vs PC IINACT/ACT 同遭遇抓包，出"parse verified/UNVERIFIED"徽章；Oodle 断流后"恢复"的遭遇标"partial/do-not-rank"绝不上传。

**(R6) "PC ACT 兼容"宣称会误导用户。** IINACT 能这么说因它**跑真插件二进制**；本方案是**从零复刻**投影层，逐字节等价是未证明的主张。诚实标注应为："输出 ACT 格式日志行；与 FFXIV_ACT_Plugin 的字段级一致性为复刻实现，每补丁未经验证。" 匹配日志**格式** ≠ 匹配日志**语义**。

**(R7) 法律/分发风险【对"可分发产品"一票否决】。**
  - **个人本地自用**：机制成立，可隐于私人范围，作者自维护即可苟活。
  - **公开分发（GitHub APK / 上架 / 任何下载链接）= 一票否决**，三发独立 kill shot 任一成立即死：
    1. **Oodle 再分发侵权**：APK 必须打包闭源 Oodle Network native 库，对 RAD/Epic 专有库再分发侵权（独立于 SE），**无合法 ARM64 授权途径**。
    2. **反规避**：主动解密 Brokefish 规避技术保护措施，在 DMCA 1201/同类法下是独立可诉项，与游戏 TOS / 是否商用无关。
    3. **SE 先例**：PlayerScope 被 SE C&D + 强制删库；本方案含**主动 MITM + 解密 + 改写**官方流量，暴露面**严格大于** PlayerScope。
  - **封号风险**：中间人改写官方流量违反 TOS，用户自担。

**(R8) 每补丁单点维护。** §6 三处随补丁更新，依赖低巴士因子的社区逆向供应链。**FF14 补丁日(玩家最想看数据时)恰是方案必然失效时**：opcode 变→解码错位→不仅没数据，还因 Oodle 状态脱节直接拉断 PS5。

**(R9) iOS 审核/原理双死。** 见 §4：后台不让常驻监听 socket 的原理性阻断 + NE 管不到 PS5 + DNS proxy 仅限受监管设备。iOS 当服务端放弃。

---

## 9. 一句话定调

**对"只有手机的 PS5 玩家"，这条路在机制上是真活的、是唯一可行的技术骨架——但它只值得作为"愿意献祭一台专用安卓机、能忍受每补丁手工维护、且纯私人不分发"的硬核玩家实验项目去做；最大的坑是：你在用游戏随时掉线的风险，去换一个可能因官方补丁或 FFLogs 后端变更而静默拿不到、且字段投影一旦错就静默变错的 DPS 报告——绝不能当成面向普通玩家的产品来宣称，更不能公开分发（Oodle 再分发 + 反规避 + SE 先例三重法律一票否决）。**

---

### 关键来源

- TemporalStasis（无注入 lobby/zone 代理 + Brokefish + EnterWorld 改写）：https://github.com/NotNite/TemporalStasis
- FF14 登录流程 / lobby 协议：https://wiki.xiv.zone/Lobby_Server ; https://docs.xiv.zone ; https://notnite.com/blog/ffxiv-login-process
- Machina（Oodle 解压）：https://github.com/ravahn/machina
- Unscrambler（去混淆六表）：https://github.com/perchbirdd/Unscrambler
- FFXIVOpcodes：https://github.com/karashiiro/FFXIVOpcodes
- IINACT（ACT 兼容蓝本）：https://github.com/marzent/IINACT ; https://www.iinact.com/
- cactbot LogGuide（日志行格式）：https://github.com/OverlayPlugin/cactbot/blob/main/docs/LogGuide.md
- OverlayPlugin WS：https://github.com/OverlayPlugin/OverlayPlugin ; https://overlayplugin.github.io/OverlayPlugin/devs/
- FFLogs 上传逻辑参考：https://github.com/MirisWisdom/fflogs.uploader ; https://www.fflogs.com/api/docs
- 安卓前台服务类型：https://developer.android.com/develop/background-work/services/fgs/service-types
- PS5 不支持 DoH/DoT、可手动 DNS（背景）：NordVPN / PIA / MakeUseOf 指南
- 主机 DNS MITM 先例：Atmosphère dns_mitm（https://switch.hacks.guide）
