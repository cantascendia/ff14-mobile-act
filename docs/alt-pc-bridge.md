# FF14 主机端 ACT —— PC 串接「零额外硬件」网络方案

> 目标：让 PS5 / NS2 玩家**不买任何专用硬件**，用已有的 PC 当串接点+解码器，手机/平板开网页看实时 ACT overlay。
> 撰写：2026-06-01。技术结论区分「已证实 / 推测」，附来源。

---

## 0. 一句话定调

- **网络-only 解码技术上成立（无需注入/读内存）** —— seed 来自网线、去混淆表静态可离线提取、Oodle 纯流重建、已有无注入开源链路。这是密码学层的硬结论。
- **PC 当串接点是最优形态**：解码栈本就是 .NET，PC 上原生跑、几乎零移植，算力充裕；比买盒子更省、更强。
- **手机当不了抓包端**（非 root 安卓物理上看不到主机流量，iOS 完全不行）；手机只能当「开网页看 overlay」的显示端。
- **不可消除的门槛**：主机封包必须物理穿过 PC → 主机网络要指向 PC，且抓包要先于主机联网启动。这一步无法绕过；能做到的是把它压成「一次性配置 + 日常零操作」。
- **定位**：个人自用 / 开源玩具可行；**做成对外收费/上架产品不可行**（法律 + 单点维护，见 §7）。

---

## 1. 为什么网络-only 解码成立（make-or-break 已验证）

四个核心环节逐一证实，**没有任何一处需要从客户端内存读运行时状态**：

1. **seed 完全来自网线封包。** Unscrambler 的 `KeyGenerator74.GenerateFromUnknownInitializer()` 只从初始化包读 `mode/seed1/seed2/seed3`，密钥由 seed + 静态表算出；`key = localRand + packetRand + derive(...)`、使用时 `keyToUse = key - localRand - lastPacketRand`，二者**代数上完全抵消**，生产代码全程不引用内存随机数。
   - https://github.com/perchbirdd/Unscrambler
2. **去混淆六张表每补丁静态、可离线提取。** `Unscrambler.DataGenerator` 直接读 `ffxiv_dx11.exe` 的 `.rdata` 段在固定偏移 dump 出 `table0/1/2 / midtable / daytable / opcodekeytable`，表很小（KB 级）。
3. **Oodle Network TCP 解压纯靠流重建。** `Machina.FFXIV/Oodle/OodleTCPWrapper.cs` 用 `new byte[0x100000]` 空窗口 + `OodleNetwork1TCP_Train(..., IntPtr.Zero, IntPtr.Zero, 0)`（不加载任何预训练字典），解码状态完全由「从首包起的流」逐包构建。
   - https://github.com/ravahn/machina/blob/master/Machina.FFXIV/Oodle/OodleTCPWrapper.cs
4. **已有「纯网络、无注入」开源链路。** `TemporalStasis`（inline MITM 代理，README 明确「不改安装、不注入进程」）+ `Unscrambler`（去混淆，覆盖 ActionEffect01/08/16/24/32、StatusEffectList/3、ActorControl、ActorCast、PlayerSpawn、NpcSpawn 等全部战斗包）。
   - https://github.com/NotNite/TemporalStasis

**串接 = 无损（关键前提）：** inline 设备在数据路径上，丢帧与主机共享并由 TCP 重传修复，设备随后看到重传 —— 与 SPAN/镜像「尽力复制、过载丢包、主机无感」有本质区别，后者正是杀死有状态 Oodle 的失配。**「从首包起」的真实单位是每条 TCP 连接的 Oodle 上下文**（Machina 按 `connection.ID` 维护独立状态），所以只要「抓包先于主机连接」即可，无需复杂仪式。

---

## 2. 推荐架构：PC 串接 + 本地解码 + 局域网网页

### 2.1 数据流（文字版架构图）

```
┌──────────┐  ①裸TCP  ┌──────────────────────────────────────────────┐
│ 路由器/   │ ───────► │  你的 PC（Windows）                            │
│  光猫     │          │                                                │
│ (上联)    │ ◄─────── │  [NIC-A 上联]  ──┬── 转发路径 ──┬── [NIC-B 主机]│
└──────────┘          │                  │ (桥接/共享)   │              │
                      │                  ▼               ▼              │
┌──────────┐  ②裸TCP  │            ┌──────────────┐  ┌──────────────┐  │
│ PS5/NS2  │ ◄──────► │            │ Npcap 抓包    │  │ 流量穿透,    │  │
│ (主机)   │          │            │ (从SYN+首字节)│  │ 主机如直连    │  │
└──────────┘          │            └──────┬───────┘  └──────────────┘  │
                      │                   ▼                             │
                      │  ③ TCP重组 + 按 connection.ID 维护独立 Oodle    │
                      │  ④ Oodle 解压(空窗口/空训练, oodle-net-shared)  │
                      │  ⑤ 去混淆: 抓 init 包取 seed → KeyGenerator7x   │
                      │     + 六张离线表 → per-opcode key, seed 前向携带 │
                      │  ⑥ opcode→语义映射(FFXIVOpcodes, 区服分支)      │
                      │  ⑦ 结构化战斗事件                               │
                      │  ⑧ 本地 WebSocket + 静态 overlay 网页(同机)     │
                      └───────────────────┬────────────────────────────┘
                                          │ ⑨ WebSocket push(局域网)
                                          ▼
                      ┌──────────────────────────────────────────────┐
                      │  手机 / 平板 / 第二显示器 浏览器               │
                      │  http://<PC局域网IP>:端口 → 实时 ACT overlay   │
                      │  (不装 app、不配置、多设备可同时看)            │
                      └──────────────────────────────────────────────┘
```

### 2.2 让 PC 变成串接点的三种接法（按推荐度）

| 接法 | 是否额外硬件 | 是否引入双 NAT | 适用 | 备注 |
|---|---|---|---|---|
| **① Windows 网桥（L2 Bridge）** ★推荐 | 一个 USB 网卡 ~$10（若 PC 只有一个网口） | **否**（L2 桥接不做 NAT） | 主机用网线接 PC | 最干净：主机拿到的还是路由器的 IP，**不影响 FFXIV 匹配/组队/DC 旅行**。抓包抓在桥接网卡上。 |
| **② Windows 移动热点** | 无（PC 有 WiFi 即可） | **是**（热点是 NAT） | 主机用 WiFi 连 PC 热点 | 最省事、真·零硬件；但双 NAT **必须实测**任务搜索器/副本匹配/世界访问是否正常。 |
| **③ ICS 网络共享** | 视情况 | 是（NAT） | 同上 | 与②同类，更老式，一般优先用②。 |

> **建议**：能接网线就用 **① 网桥**（避免双 NAT 这个最大不确定性）；纯无线场景用 **② 移动热点**，但先跑一遍匹配测试。

### 2.3 PC 路线相对「买盒子」的硬优势

- **解码栈零移植**：Machina / Unscrambler 都是 C#/.NET，Windows 原生直接跑；盒子要交叉编译到 MIPS/ARM + 带对应架构 Oodle 原生库。
- **算力充裕**：去混淆是逐字节查表+减法，PC 毫无压力；复盘/录像/SQLite 也能顺手做。
- **零采购**：用户已有 PC，唯一可能花的钱是 ~$10 USB 网卡（仅网桥接法、且 PC 只有单网口时）。

---

## 3. 解码栈技术清单（复用/移植组件）

| 组件 | 用途 | 仓库 | 维护方 / 是否要自己跟 |
|---|---|---|---|
| **Machina.FFXIV / Oodle 封装** | Oodle 解压、FFXIVBundleDecoder 解帧、按 connection.ID 维护状态 | https://github.com/ravahn/machina | ravahn 等，补丁日响应较快。**需跟，相对健康。** |
| **Unscrambler** | KeyGenerator72/73/74、六张离线表提取、per-opcode key、ObfuscatedOpcodes 清单 | https://github.com/perchbirdd/Unscrambler | **perchbirdd 一人（巴士因子=1）。最危险单点，必须自己镜像/贡献。** |
| **去混淆表 + VersionConstants** | 每补丁从 .exe 重新提取 | （由 Unscrambler.DataGenerator 产出） | **必须自己跑、自己版本化存档**（上游删库即断供）。 |
| **FFXIVOpcodes** | opcode→语义映射，每补丁、每区服（Global/CN/KR/TW + NS2 分支） | https://github.com/karashiiro/FFXIVOpcodes | 多人维护，最稳一环，但仍需每补丁每区服跑。**需跟。** |
| **TemporalStasis** | 无注入 inline 代理参考实现 | https://github.com/NotNite/TemporalStasis | NotNite。参考架构为主。 |
| **oodle-network-shared.dll** | Oodle 原生编解码器（非开源，RAD/Epic） | RAD Game Tools | **许可需法务评估**；本地自用敞口小于分发。 |

**自己必须扛的活**：① 每补丁重新提取去混淆表 + 版本常量并存档；② opcode 表区服分支跟进；③（强烈建议）派人进 Unscrambler/FFXIVOpcodes 当 contributor，把巴士因子从 1 提到 2–3，沉淀自己的 jump-table diff 逆向能力 —— 这是「断供可自救」的唯一路径。

---

## 4. 用户 UX 流程（把门槛压到最低）

```
【一次性配置（不可避免，永久免重复）】
  1. PC 装你的程序（含 Npcap）。
  2. 选接法：网线→用网桥(①)；只能无线→用移动热点(②)。
     程序自动配置桥接/热点。
  3. 主机网络设置：连到 PC（网桥则插网线即可；热点则连 PC 的 WiFi），保存。

【日常使用（「开网页即用」兑现点）】
  4. 开 PC → 程序随系统自启 → 抓包+解码已在后台跑（先于主机联网）。
  5. 主机正常开 FFXIV/NS2。
     —— 因为抓包先于连接,天然满足「从首包起」,无需先开抓包再登录的仪式。
  6. 手机/平板/第二屏 浏览器打开 http://<PC-IP>:端口
     → 不装 app、不登录、不配置 → 立刻看到实时 overlay,多设备可同看。

【补丁日（用户无感）】
  7. 程序静默拉取你签名的表包(opcode+去混淆表),校验热加载。
     上游已适配 → 下一场战斗自动恢复;未适配 → 显示「等待 7.x 适配」占位而非崩溃。
```

**门槛边界说清楚**：
- **不可避免的一次性**：主机网络指向 PC（第 3 步）。任何能看到主机裸 TCP 的方案都得在数据路径上插一个东西，这是物理本质。
- **零配置、开网页即用**：第 4–7 步全自动，没有配对码/账号/云。

---

## 5. MVP 里程碑

| 里程碑 | 交付 | 退出条件 |
|---|---|---|
| **M0 · 主机可解性验证**（最高优先） | PC 串接抓 PS5/NS2 的 FFXIV 流 → 离线喂进 TemporalStasis+Unscrambler+Machina 管线 → 解出**哪怕一个** ActionEffect 包。 | **证实「主机流与 PC 流一致」这个推断**（两库只在 PC 演示过，主机端到端无公开报告）。这步不过，后面免谈。 |
| **M1 · 个人 DPS（最小可用）** | 实时管线：单连接 Oodle+去混淆+解 ActionEffect → 只出自己 raw DPS + 逐技能构成。本地 WebSocket + 极简 overlay 网页。 | 网页看到自己实时 DPS，与游戏内伤害飘字吻合。 |
| **M2 · 全队 raw DPS + HPS** | 多连接管理 + PlayerSpawn/NpcSpawn 建实体表 + 治疗分支。 | 8 人本正确归属每人伤害。 |
| **M3 · 时间轴 + buff uptime + 点名** | StatusEffectList/3 + ActorCast + ActorControl/TargetIcon。 | 时间轴相对顺序正确；点名标记出现。 |
| **M4 · rDPS（标注「估算」）** | 引入 buff→伤害加成模型表，输出估算 rDPS，UI 显式标「估算，与 FFLogs 口径有偏差」。 | 见 §6 数据完整度，不撒谎。 |
| **M5 · 签名 OTA 表包 + 补丁日健康度** | 固化公钥只接受签名表包；监测上游适配滞后预警。 | 补丁日用户无感恢复。 |

---

## 6. 数据完整度（必须对用户讲清，否则过度承诺）

- **【能如实给】** 自身/全队 **raw DPS**（服务器权威值）、逐技能构成、暴击/直击、**raw HPS**、buff/debuff **uptime**、读条与点名时间轴、团灭/复活节点。这是网络-only 的硬通货。
- **【只能近似】** effective HPS / overheal（依赖累加估算的 HP 池）、绝对墙钟时间（时间戳含 RTT 抖动，相对顺序用服务器序号更准）。
- **【不能算，只能估】** **FFLogs 口径的 rDPS / expected damage 不是网络-only 能「算」的量** —— 需要网络包之外的 buff→伤害加成模型（乘区、加成%、buff 来源归属），本质是「重建估算」。**必须降级标注，别当 raw DPS 一样承诺。** 另外 buff 的精确来源归属依赖 StatusEffect 是否稳定带 sourceId（需验证，同职业多人提供同名 buff 时尤其存疑）。

---

## 7. 诚实风险清单

1. **每补丁维护单点（结构性）**：去混淆表实质系于 perchbirdd 一人的业余时间，无合同无报酬；历史上个别战斗包数月才正确去混淆（headmarker 拖到 2025-05、ActorCast 拖到 2025-08）。且 **PC 社区整体在从「网络解码」转向「注入解码」**，你押的是人力正在撤离、而注入对主机不可用的赛道。缓解：进上游当 contributor + 镜像所有产物 + 补丁日健康度预警。**只能延缓不能消除。**
2. **数据完整度**：rDPS 只能估算（见 §6）。即使法律/维护都解决，玩家最在意的竞技指标仍是估算值。
3. **合规 / 封号（对「公开产品」是 BLOCKING）**：SE《Regarding Third-party Tools》一刀切禁止 packet 类 + 显示服务器数据的 UI + 公开推广；对 PlayerScope 已动用「正式 C&D + 强制永久删库」。**严格个人自用、不分发盒子/程序、不托管云、不推广** 能显著降风险，但等于放弃「产品化」。账号风险永远非零。
4. **双 NAT / 联机破坏**：移动热点(②/③)会引入第二层 NAT，可能影响任务搜索器/副本匹配/DC 旅行 —— **必须在目标区服实测**。**网桥(①, L2)不引入 NAT，是规避此风险的首选。**
5. **串接单点 = 主机断网**：PC 睡眠/程序崩/拔线都会让主机断网。缓解：程序开机自启 + watchdog 自拉起 + 「一键切回主机直连」说明。
6. **Oodle 原生库许可**：链接 oodle-network-shared.dll 存在许可问题，本地自用敞口小于分发，需法务评估。
7. **NS2 适配**：结论与 PS5 完全一致 —— 同样封闭、同样不可注入、同样只能串接，本方案硬件/链路对 NS2 直接适用（L2 桥接对设备类型不敏感）。NS2 只**加重维护**（多一条 opcode/表分支，错配会全盘解析错而非局部错），且主机端到端跑通在 NS2 上同样需 M0 单独实测。

---

## 8. 关键来源

- Unscrambler（去混淆/seed/表）：https://github.com/perchbirdd/Unscrambler
- Machina（Oodle 解压）：https://github.com/ravahn/machina
- TemporalStasis（无注入 inline 代理）：https://github.com/NotNite/TemporalStasis
- FFXIVOpcodes：https://github.com/karashiiro/FFXIVOpcodes ; https://github.com/SapphireServer/FFXIVOpcodes
- Sapphire《Fixing Opcodes》（opcode 逆向方法学）：https://sapphireserver.github.io/dev/2019/12/23/fixing-opcodes.html
- 非 root 安卓抓不到 tethered 流量：https://emanuele-f.github.io/PCAPdroid/faq.html ; https://github.com/emanuele-f/PCAPdroid/issues/20
- SE 第三方工具政策：https://na.finalfantasyxiv.com/lodestone/topics/detail/36c4d699763603fadd2e61482b0c5d56cb2e4547
- PlayerScope 删库先例：https://www.pcgamer.com/games/final-fantasy/ff14-stalker-plugin-playerscope-shut-down-after-creator-says-they-were-sent-a-cease-and-desist-though-thats-not-quite-the-end-of-it/
