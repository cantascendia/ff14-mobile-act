# FFXIV 主机端 ACT 可行性深度报告：PS5 与 Switch 2

## 1. 执行摘要

**一句话结论：在不越狱、不外接专用硬件的前提下，PS5 和 Switch 2 上今天没有、且短期内也不会有任何"开箱即用"的实时 DPS 解析方案；最现实的路线只有两条——(A) 同账号在 PC/Steam Deck 上重玩并用 IINACT/ACT 解析（已证实成熟，违反 ToS），或 (B) 主机分享键录像后人工对照 FFLogs（合规但极繁琐）。** 任何"把主机流量镜像到 PC 被动抓包"的旧路线，自 7.2 补丁起已被官方混淆机制 + 有状态 Oodle 压缩双重打死；任何"在主机上跑代码"的路线，被主机封闭平台从物理上堵死。这是两堵彼此独立的硬墙，任何一堵单独成立就足以否决"主机端原生解析"。

> 关键澄清（已证实）：本报告所称"主机端无解"指的是**零售/未越狱主机**、且追求 **ACT/FFLogs 级完整事件流**。降级到"屏幕可见数字"的 HDMI+OCR 路线在物理上可行但数据残缺，详见第 6 节。

---

## 2. ACT 工作原理：memory 模式 vs network 模式

ACT（Advanced Combat Tracker）本身只是个聚合器，FFXIV 的数据采集由一条插件链完成。理解这条链是理解"主机为什么不行"的前提。

**完整数据链路（自下而上，已证实）：**

1. **取包层 — Machina**（`ravahn/machina`）：定位游戏 TCP 流、做 IP 分片重组与 TCP 流重组、Oodle 解压，输出 FFXIV IPC 消息。
2. **解码层 — FFXIV_ACT_Plugin**（闭源）：用每版本更新的 opcode 表把 IPC 消息翻成"日志行"，并辅以少量内存读取补全。
3. **聚合层 — ACT 主程序**：算 DPS/HPS/治疗统计。
4. **分发层 — OverlayPlugin**：把日志行通过进程内 API 或本机 WebSocket（`ws://127.0.0.1:10501/ws`）暴露给前端。
5. **消费层 — Cactbot 等**：做点名/AOE/时间轴提醒。

**官方插件 README 原文（已逐字核实）：** FFXIV_ACT_Plugin "reads a combination of **memory and network data** from your local pc"。这句话是整份报告最关键的一句——它意味着标准 ACT 链同时依赖内存和网络，**两者都只能取自本机运行的游戏进程**。

### network 模式（包解析）= 主路径

network 模式覆盖了绝大部分战斗与机制数据，且比内存模式更准、受补丁内存布局变化影响更小：

| 日志行 | 事件 | 来源 |
|---|---|---|
| 21 NetworkAbility | 伤害/治疗（Action Effects） | 网络 |
| 22 AOEAbility | 范围技能 | 网络 |
| 26/30 | Buff 增/移除 | 网络 |
| 20 StartsCasting | 读条 | 网络 |
| 27 HeadMarker | 点名 | 网络 |
| 35 Tether | 连线 | 网络 |
| 37 ActionSync / 39 UpdateHP | HP 变化 | 网络 |
| **261 CombatantMemory** | **任意单位精确实时 HP/最大 HP、高频坐标** | **内存轮询** |

### memory 模式 = 辅助补全（非历史"全内存模式"）

需要区分两个概念：
- **历史的"全内存模式"**：早年纯靠读游戏内存解析。已非主流，因内存布局每补丁可能变化而易失效，且不如包解析准确。
- **当前的"内存补全"**：仅对少数字段（如行 261 CombatantMemory 的精确 HP/坐标）做内存轮询。

**为什么 network 模式是主机端的"理论关键"：** 战斗事件（伤害/治疗/buff/点名）本质上是**服务器权威**的 IPC 包，原则上不需要读客户端内存。这给"被动抓包重建战斗流"留下了一条理论缝隙——如果能在线缆上拿到并解出这些包，理论上就能重建大部分战斗数据，无需在主机内跑代码。第 3 节将裁决这条缝隙今天是否还走得通。

> 注：现代取包已从历史的 raw socket/pcap 转向 **Deucalion 注入**——往 `ffxiv_dx11.exe` 注入 DLL，hook"读取已解码包"的函数，经命名管道 `\\.\pipe\deucalion-{PID}` 广播游戏内部已解压、已去混淆的包。这条路对主机天然不可用（无法注入）。

---

## 3. 核心可行性裁决：被动抓包今天能否解析 FFXIV 战斗数据？

**总裁决：不能。** 被动抓包路线在 2025 年已被两堵独立的技术墙打死，主机平台还额外叠加第三堵架构墙。下面逐一拆解，并区分"已证实/推测"。

### 三层协议结构（已证实，需澄清"加密 vs 混淆"）

这是最容易被误传的一点。FFXIV 协议分三层处理，**战斗数据所在的 zone 服务器并未被强加密**：

| 服务器 | 处理方式 | 对抓包的影响 |
|---|---|---|
| Lobby（登录/选角） | **加密**（Blowfish，社区昵称 "Brokefish"） | 不含战斗数据，无关 |
| Zone（战斗数据） | **Oodle 压缩 + 部分包部分字段混淆** | 这才是战斗解析的战场 |

> **澄清（已证实）：** 把 zone 战斗包说成"被加密"是不准确的。它是**压缩 + 选择性字段混淆**，不是 TLS/Blowfish 强加密。这一点很重要——它意味着混淆在数学上**可逆**，理论上拿到完整会话流 + seed 就能离线还原。但"理论可逆"距离"实践可用"非常远，见下文两堵墙。

### 硬性阻碍 #1：Oodle 有状态压缩（自 6.3 补丁，已证实）

自 6.3 起 zone 协议改用 **Oodle Network 有状态 TCP 压缩**：每条 TCP 连接维护一个持续更新的字典/滑动窗口（Machina 实现 `OodleNetwork1TCP_Decode`，1MB 窗口）。后续包通过反向引用早先数据来解压。

**致命后果：** 必须**从连接建立的第一个包**开始抓，才能构建解码字典；**任何丢包都会永久性破坏解码**，直到玩家退回角色选择界面重连。`FFXIV-Packet-Dissector` README 原文确认："it now requires a socket level state to retrieve the raw packet data and therefore the dissector can no longer directly read the communication."

这对**旁路镜像**是结构性灾难：SPAN 端口在负载下丢镜像帧、ARP-MITM、Wi-Fi 监听都会丢包；Machina 自己也警告"在连接发起到监听器启动之间很可能丢失部分网络数据"。

### 硬性阻碍 #2：包混淆（自 7.2 补丁，2025-03-25，已证实）

7.2 对**正是 DPS/时间轴所需的战斗包**加了一层混淆：ActionEffect（1/8/16/24/32 目标变体）、StatusEffectList、ActorControl、ActorCast，外加 PlayerSpawn/NpcSpawn 等。

- **机制**：基于 key 的 XOR + 减法（可逆算术，非密码学）。
- **依赖每会话 seed**：7.2 在 InitZone 包下发；**7.3** 把常量改为**服务器派生的 per-opcode key**；**7.4** 起 InitZone 不再含 seed，改由一个**专用初始化包**（社区记为 opcode 702）独立下发，成为唯一初始化途径。
- **每补丁查表变化**：去混淆查表从游戏 `.exe` 提取，每补丁都变，靠 `perchbirdd/Unscrambler` 等少数志愿者项目维护。

> **重要纠正（来源归属）：** "7.2 后只剩 Deucalion、raw/pcap 不再支持且无恢复计划"这句精确措辞**出自 OverlayPlugin setup 文档**（"as of patch 7.2 the only remaining option is Deucalion"）与 FFXIV_ACT_Plugin **发布说明**（v2.7.3.1："Raw network packet capture and pcap are no longer supported, and there are no plans to get them working again"），**不是** ravahn/FFXIV_ACT_Plugin 的 README（该 README 至今仍并列列出三种模式、不提混淆）。事实成立，但核查者请认准正确来源。

### 叠加阻碍 #3（仅主机）：opcode 每补丁随机化（已证实）

独立于混淆，SE 每个版本都打乱所有 IPC opcode 枚举值（Sapphire 开发博客原文："SE now randomises all client and server opcode enums"）。即便你解出了压缩 + 混淆，没有当前补丁的 opcode 表也无法解释字节含义。

### 分平台裁决

#### PS5

- **能否被动抓包解析？不能。** 即便把 PS5 流量镜像到 PC，你拿到的也是 Oodle 压缩 + 字段混淆的字节流。要还原需要：从连接起点零丢包抓包（旁路镜像难保证）+ 抓到那个随机 opcode 的 seed 初始化包 + 复现当前补丁的去混淆查表 + 当前补丁 opcode 表。**没有任何现成的、在 7.2+ 维护的远程被动工具存在。** 历史上 AyCT 这类"主机零代码"工具在未混淆时代曾理论可行，但该项目 stale（仅 7 commits、FFLogs 集成仅 planned），7.2 后彻底失效。
- **真正的根本阻碍（已证实，比抓包更致命）：** PS5 是封闭平台，**无法注入 DLL、无法运行 Dalamud**。所有维护中的工具都需要在跑游戏的机器上执行代码——PS5 做不到。ACT 论坛资深回答者 EQAditu 原话："There's no chance that will work for a PS4 or even Mac... the parsing plugin will more or less refuse to work without an active FFXIV process to monitor."
- **结论：PS5 上原生解析在架构上、协议上、合规上三重不可行。**

#### Switch 2

- **能否被动抓包解析？同样不能**，且理由完全等价于 PS5——封闭主机不能跑代码（推测，基于平台性质：官方未公布 NS2 版技术细节，但 Nintendo 主机封闭性与 PS5 同级），且同样受 6.3 Oodle + 7.2 混淆约束。
- **额外不确定性：** NS2 版需**单独购买 + 单独订阅**（已证实），这强烈暗示其账号/计费体系独立。但**是否与 PC/PS5 跨平台同服尚有微妙之处**：媒体报道（Game Informer、Nintendo Inquirer）称所有版本共享服务器、支持跨进度，符合 FFXIV 单一世界模型；但**官方 Lodestone 公告原文对跨平台同服只字未提**（推测为"高度可能但未经官方明文确认"）。这直接影响第 8 节里"跨平台同账号在 PC 解析"workaround 是否对 NS2 玩家成立。
- **结论：Switch 2 上原生解析同样三重不可行；时间线见第 7 节。**

---

## 4. 方案全景表

评级图例：可行性（✅可用 / ⚠️理论可行无成品 / ❌不可行）；数据完整度（满分=ACT/FFLogs 级完整事件流含来源/技能/暴击直击/rDPS）；上手难度（普通玩家视角）；成本；封号与合规风险。

| 方案 | 可行性 | 数据完整度 | 上手难度 | 成本 | 封号/合规风险 |
|---|---|---|---|---|---|
| **ICS 网关 / Wi-Fi 热点**（PC 当主机网关） | ❌ 解析不可行（仅解决连通性） | 0（拿到的是压缩+混淆字节） | 低（插网线） | $0 | 抓包违反 ToS；公开使用有举报风险 |
| **托管交换机端口镜像（SPAN）** | ❌ | 0 | 中（需管理型交换机） | $30–150 | 同上；旁路镜像在负载下丢包→Oodle 永久 desync |
| **ARP 欺骗 / MITM** | ❌ | 0 | 中（Ettercap 等，脆弱） | $0 | 同上；最不可靠，易致主机断网 |
| **TAP 硬件分路器** | ❌（取字节可靠，但解不出） | 0 | 中（接线+双 NIC/聚合 TAP） | $30–数百 | 同上 |
| **路由器镜像（OpenWrt tc/iptables-tee、pfSense bridge-SPAN）** | ❌ | 0 | 高（CLI/UCI） | $0–200 | 同上 |
| **树莓派/双网口透明网桥** | ❌（取字节最可靠，仍解不出完整流） | 0（被动）；理论需移植 Unscrambler | 高（Linux） | $35–75 | 同上 |
| **HDMI 采集卡 + CV/OCR** | ⚠️ 可行但数据残缺 | 低（仅可见数字，无来源/技能/rDPS） | 高（自建 CV 工程+持续调参） | $50–200+ | **技术检测面最干净**（不碰进程/内存/网络）；仍受 ToS 政策约束 |
| **云中转（raw 流送云端去混淆 SaaS）** | ⚠️ 纯推测无成品 | 中（若能解出）；丢包即 desync | 中（装本地 agent） | 订阅+云 | 高：分发逆向查表是法律首要打击目标；contentID/角色名外传隐私问题 |
| **硬件一体盒（inline 网桥/网关，盒内移植 Unscrambler）** | ⚠️ 纯推测，无任何主机成品先例 | 高（若打通）；每补丁碎两层 | 极高（持续逆向） | 自研 BOM | 高：违反 ToS，公开使用有封号先例 |
| **离线录制盒 + 赛后还原** | ⚠️ 纯推测 | 高（若打通，非实时） | 极高 | 自研 BOM | 高（同上） |
| **HDMI 硬件合成器（叠 overlay 回输出链）** | ✅（仅显示层） | N/A（不产数据） | 中–高 | $几十–数百 | 干净（只处理视频信号），但需上游数据源才有意义 |
| **第二屏/手机 WebSocket overlay** | ✅（仅显示层） | N/A | 低 | $0 | 干净，但需上游数据源 |
| **☆ 同账号在 PC/Steam Deck 重玩 + IINACT/ACT** | ✅ 成熟可用 | 满分（完整事件流+FFLogs 上传） | 中 | 已有 PC 即 $0 | 违反 ToS；私用一般不处置，公开/直播有 10 天封号先例 |
| **☆ 主机分享键录像 + 人工对照 FFLogs** | ✅ 可行 | 低（人工估算） | 高（极繁琐） | $0 | **合规**（不碰客户端/网络，纯视频） |

> 一句话读表法：**"把字节弄到 PC"是已解决的简单问题；"把字节解成战斗事件"才是真墙，而主机连"跑代码"这一步都迈不出去。** 唯二带 ☆ 的行是真正能落地的。

---

## 5. 推荐路线分步落地指南

最现实的"既要实时 overlay、又要 FFLogs"路线，**不是在主机上做任何事，而是把游戏挪到一台能跑代码的机器上**。下面给普通玩家可照做的版本。

### 路线 A（首选，追求完整解析）：同账号在 PC 或 Steam Deck 上重玩 + IINACT

**前提认知：** FFXIV 全平台跨平台同服（已证实）——同一 Square Enix 账号可在 PS5/Windows/Mac/Steam 间游玩，只要在同一数据中心。你在 PS5 练的角色，换到 PC 登录就是同一个角色。**这条 workaround 对 PS5 玩家完全成立；对 NS2 玩家则取决于第 3/7 节的跨平台同服确认。**

**Windows PC 步骤：**
1. 在 PC 上安装正版 FFXIV 客户端，用你的同一账号登录。
2. 安装 ACT 主程序，加入 FFXIV_ACT_Plugin。
3. 7.2+ 起插件会**自动注入 Deucalion**，无需手动配置 raw socket/防火墙（旧教程里的管理员权限/Npcap 步骤已过时）。
4. 务必**在进副本/登录前就打开 ACT**（Oodle 有状态约束：必须从连接起点抓）。
5. 装 OverlayPlugin + Cactbot（点名/AOE/时间轴）或你喜欢的 DPS overlay。
6. FFLogs 上传用官方 Uploader 消费 ACT 本地日志。

**Steam Deck / Linux 步骤（更省心的现代选择）：**
1. 用 XIVLauncher（XLCore）+ Proton 跑 FFXIV。
2. 安装 **IINACT**（`marzent/IINACT`，作者是 marzent，**不是** goaaats——goaaats 是 Dalamud/XIVLauncher 作者）。
3. IINACT 作为 Dalamud 进程内插件运行，**数据源仅基于 Unscrambler，无需额外 Deucalion 注入、无需提权抓包**（README 原文），CPU 占用远低于 ACT，尤其在 Wine/Proton 下。
4. overlay 用 Browsingway / BunnyHUD / HUDKit；兼容 FFLogs 与社区 overlay。

**为什么这是首选：** 它用的是维护中的成熟栈，上游每补丁自动更新（混淆/opcode 变化由社区维护者承担），你不碰任何逆向工程。代价是你在"非主机"上玩，以及 ToS 风险（见下）。

### 路线 B（追求合规，接受繁琐）：主机录像 + 人工对照

1. PS5/NS2 用**分享键**录制整场 boss 战。
2. 到 FFLogs 找同 boss、同职业的顶级 log。
3. 按战斗时长对齐，手动数你的技能次数、对照潜力值估算。

社区原帖明说"this method doesn't make you use any illegal programs"，但评论区共识是"Way too much work"。这是唯一**不违反 ToS** 的路径，代价是没有实时性、只能粗略估算。

### 合规与封号的诚实提示（不和稀泥）

- SE 官方 Prohibited Activities **明文禁止一切第三方工具**，包括"修改 UI 显示额外信息""提供反应优势"的工具；吉田直树多次重申"从未允许过"。违规可停封，重犯永封。
- **执行现实（社区观察，非 SE 书面豁免）：** SE 不扫描玩家 PC；检测主要靠"客户端向服务器发非法数据"+ 举报。被动只读（ACT/IINACT 不发包）+ 私用，社区**无已知封号先例**（XIVLauncher/Dalamud FAQ 原文）。**真实风险来自公开炫耀/直播被举报**——曾有直播开插件被封 10 天的案例。
- 一句话：**私用 + 闭嘴 ≈ 实践中低风险，但绝非官方许可，账号风险永远非零。**

---

## 6. 创新与前瞻方案（整合三种视角）

下列方案整合"硬件优先""云/VM""AI/视觉"三种视角，按成熟度标注。**没有一个能在主机上原生跑通完整解析**——它们要么把游戏挪走，要么退而求其次。

### 成熟度：proven（已验证可用）

**① 云游戏 VM 透传（Cloud-game-VM passthrough）**
在自建云端 Windows GPU VM 里跑真实 FFXIV，IINACT/Deucalion 正常工作，画面串流到主机/手机当瘦客户端，overlay 推手机。**本质是"在云端玩 PC 版"**，彻底绕过主机封闭性与抓包/Oodle/seed 全部难题。代价：你玩的是云 PC 不是主机；延迟与月费；ToS 风险照旧。

**② 源无关的手机/WebSocket overlay**
复刻 OverlayPlugin WS 协议的手机/电视网页 app（cactbot 兼容），消费任意上游（云 VM/视觉内核/OCR）。这是主机原生无法叠 overlay 的唯一显示出路。纯客户端软件、不随补丁碎。**但它只是显示层，单独零数据。**

### 成熟度：plausible（工程可行，需自建，易碎）

**③ HDMI 采集 + CV/OCR 专用盒**
主机 HDMI → 采集盒 → 计算机视觉管线。两个工程升级值得注意：
- **区域化 VLM 读战斗日志**：锁定固定 ROI 的战斗日志框（比飘字稳定得多），用小型多模态模型做行级识别，鲁棒性远超 Tesseract。
- **飘字时空追踪**（类 ByteTrack）：把伤害飘字当运动目标多帧聚合，抵消单帧漏读；飘字的颜色/字号天然编码暴击/直击/方向，可补回战斗日志缺的标记。

唯一不受 SE 协议演进影响的路线（不读内存/不抓包），反作弊技术面最干净。**硬伤是数据本质残缺**：FFXIV 可见文本缺伤害来源/技能归属，DoT 与宠物伤害混在一起，**算不出 rDPS**；高密度团本飘字被 AOE 遮挡、滚动过快，30/60fps 下精度显著下降（推测，未经 FFXIV 实机基准）。

**④ HUD 状态向量化 + 机制提醒优先模式**
对位置固定、外观稳定的 HUD 元件（血条像素长度→HP%、Boss 读条、buff 图标、点名标记、连线）做模板匹配（对固定字号可近 100%）。**务实定位：放弃精确 DPS，主打 Cactbot 式机制提醒**（读条/点名/AOE 预兆→TTS 语音）——这对主机玩家"少死人"的价值远高于看数字，且识别可靠度高、无需归因。**注意：SE 恰恰把"提供反应优势的提醒类工具"列为优先打击对象，此路线合规风险高于纯 DPS。**

**⑤ PC 真值自举训练视觉模型**
用"网络方案"喂"视觉方案"解决 ML 标注成本：一名玩家在 PC 跑 ACT 拿带时间戳的真值事件流，同时采集卡录同屏画面，按时间戳对齐 = 海量自动标注的（画面→真值事件）训练对，零人工标注。训练出的模型再独立部署到主机采集场景。**两条路线最强互补点：网络当老师、视觉当学生。** 代价：训练阶段 PC 端 ACT 仍违反 ToS，且 PC/主机渲染分布有差异需域适应。

**⑥ 事后录像批处理解析（非实时最高精度档）**
分享键录像离线送大模型逐帧精解析 + 全局时序平滑，输出接近 ACT 的事后战报。直接自动化第 5 节路线 B 的"人工对照"步骤。仍受像素层缺字段限制，DPS 仍是估算。

**⑦ HDMI 硬件合成器叠加盒**
视频 OSD/PiP/树莓派 HVS 把 overlay 烧进**采集/直播输出那一路**（不是回给玩家电视的帧）。现成硬件存在。可与 OCR 盒物理合一。**仅显示层，必须接上游数据源。**

### 成熟度：speculative（纯推测，无主机成品先例）

**⑧ Inline 透明网桥 / 网关一体盒（盒内移植 Unscrambler）**
双网口设备物理串接，对穿过流量零丢包从连接起点抓 Oodle 有状态流 + 抓 seed 包，盒内移植 Machina Oodle 解压 + Unscrambler 去混淆 + 当前 opcode 表，本盒起 WebSocket 推手机。**这是唯一"机制上可逆"的完整事件流路径**，且因纯包字节+seed+查表、不读内存，恰好规避了主机无法注入的限制。**但：每补丁碎两层（opcode 随机化 + 混淆/seed 位置变化，7.2→7.4 已变两次）、依赖单人维护项目、inline 盒故障即主机断网、且无任何"主机经路由跑通实时 DPS 并上传 FFLogs"的公开先例。** 网关变体（包装成"游戏路由器"）更易消费化，但引入双 NAT→Strict NAT，可能破坏组队匹配/任务搜索器/语音（对 FFXIV 未实测，通用证据强）。

**⑨ 离线会话录制盒 + 赛后还原**
实时只做无丢包 dump（含登录时刻 seed/init 包），赛后离线解压+去混淆+重建完整事件流。去掉"实时"这一最难约束，专攻"完整可逆性"——正是"拿到含 seed 完整会话流理论可离线还原"的精确场景。代价：丢失起始 seed 包整条会话报废；放弃实时=放弃 ACT 最大卖点。

**⑩ 托管重建 SaaS（云中转）/ 双账号影子小队 / 视觉-网络混合校准**
- SaaS：本地 agent 抓 raw 流送云端（持有当前补丁查表）去混淆——把每补丁逆向集中化变现。**法律风险最高**：分发逆向查表是 SE 首要打击目标，且 contentID/角色名外传。
- 影子小队：云 VM 跑第二个正版账号同房同队，靠小队广播的 ActionEffect 包反推主机玩家伤害。需第二份订阅、同副本，单刷/随机匹配无效，挂机账号易被封。
- 视觉-网络混合校准：同房有 PC 玩家时，用其 ACT 真值实时校准主机视觉估算。强依赖同房 PC 玩家，纯架构推断无先例。

---

## 7. 风险与未决问题

**已证实的持续风险：**
- **opcode 每补丁随机化** + **混淆算法/seed 分发位置每补丁变化**：7.2 引入混淆、7.3 改 per-opcode 派生 key、7.4 把 seed 移到专用初始化包——**已变两次**。任何抓包路线都需每补丁逆向更新，依赖少数志愿者（如 Unscrambler 基本一人维护）。这是"即使做出来也持续高维护成本"的军备竞赛。
- **SE 立场强硬且在主动反制**：混淆升级本身就是 SE 的反第三方工具战役；吉田重申禁令；2025-03 对 PlayerScope 发了 cease-and-desist。混淆是移动靶，不是已解决问题。
- **Oodle 有状态约束**：必须从会话起点零丢包抓包，对旁路镜像是致命放大。

**未决/推测问题（诚实标注）：**
- **NS2 跨平台同服**：媒体报道共享服务器+跨进度，但官方 Lodestone 公告原文未明文确认。若 NS2 与 PC 不同服/不同账号体系，则"在 PC 解析"workaround 对 NS2 玩家可能不成立——**这是 NS2 玩家最需要等官方明文的一点**。
- **NS2 时间线**：FFXIV 官宣登陆 Switch 2，**2026 年 8 月上线**（先 1 个月抢先体验确保服务器稳定）。当前为 2026-06，距上线约 2 个月。NS2 版的键鼠/LAN/坞站模式等技术细节官方未披露。
- **NS2 网络/外设能力**：不改变 ACT 不可行的结论（封闭主机+协议混淆双重锁死），但具体接口（USB/蓝牙键鼠、有线 LAN）官方未列。
- **HDMI+OCR 实机精度**：高密度团本飘字召回率/精度无 FFXIV 实机基准，现有判断为定性推断。
- **来源时效**：7.4 的 opcode 702、mode 字节偏移、per-patch 魔数等细节几乎全靠 Unscrambler 单一来源，且每补丁易变；当前零售线已到 7.5x（FFXIV_ACT_Plugin 3.0.1.8 支持 7.50），这些常量很可能已 stale。视作"方法示意"而非"当前规范"。

---

## 8. 针对 PS5 与 Switch 2 的最终建议

### PS5 玩家

1. **想要实时 overlay + FFLogs：** 别在 PS5 上折腾任何抓包/硬件盒（架构+协议+合规三重不可行，且无成品先例）。直接用**同账号在 PC 或 Steam Deck 重玩** + IINACT/ACT（路线 A）。跨平台同服已证实，角色互通。这是唯一成熟、可上 FFLogs 的路。
2. **想要完全合规：** 分享键录像 + 人工对照 FFLogs（路线 B），接受繁琐。
3. **只想要主机上的机制提醒（不死人）：** 若你愿意接受自建工程 + 合规风险，HDMI 采集 + HUD 视觉 + TTS 点名播报（方案 ④）是物理上唯一在主机端可做的，但要自己造、且属 SE 优先打击的"反应优势"类工具。**不推荐普通玩家自建。**
4. **底线：不要相信任何号称"路由器抓包就能在 PS5 出 DPS"的现成方案**——7.2 后它们都失效了，没有例外。

### Switch 2 玩家

1. **结论与 PS5 等价：** 封闭主机不能跑代码 + 协议混淆，原生解析不可行。
2. **先等官方明文确认跨平台同服**（推测高度可能但未明文）。**若确认同服同账号**：照搬 PS5 的路线 A，在 PC/Steam Deck 用同账号重玩解析。**若 NS2 独立计费意味着独立服务器/账号**：则连"在 PC 解析"都不成立，只剩录像人工对照（路线 B）或主机端 HDMI+OCR（残缺数据）。
3. **时间线提醒：** NS2 版 2026 年 8 月才上线（先 1 个月抢先体验）。当前（2026-06）尚无 NS2 客户端可供实测，所有 NS2 结论都是基于平台性质的推断，**上线后需以实测与官方明文为准**。
4. **唯一确定合规的路：** 与 PS5 相同——分享键录像 + 手动对照 FFLogs。

---

### 关键来源链接

- ACT 插件原理：https://github.com/ravahn/FFXIV_ACT_Plugin
- Machina 取包库：https://github.com/ravahn/machina
- Deucalion 注入：https://github.com/ff14wed/deucalion
- Unscrambler（7.2/7.3/7.4 去混淆）：https://github.com/perchbirdd/Unscrambler
- "7.2 后只剩 Deucalion" 措辞来源：https://overlayplugin.github.io/docs/setup/
- Oodle 有状态压缩：https://github.com/zhyupe/FFXIV-Packet-Dissector
- opcode 随机化：https://sapphireserver.github.io/dev/2019/12/23/fixing-opcodes.html ；https://github.com/karashiiro/FFXIVOpcodes
- IINACT（marzent）：https://github.com/marzent/IINACT ；https://www.iinact.com/
- "PS4/Mac 不可行" 论坛裁决：https://forums.advancedcombattracker.com/discussion/102/proxy-through-pc-to-read-ps4-ff14-packets ；https://github.com/ravahn/FFXIV_ACT_Plugin/issues/216
- PS5 录像对照 FFLogs：https://forum.square-enix.com/ffxiv/threads/505556
- SE 第三方工具政策：https://support.na.square-enix.com/faqarticle.php?id=5382 ；吉田 2022 声明 https://na.finalfantasyxiv.com/lodestone/topics/detail/436dce7bd078c914009957f2221c13e6a5cb497d
- 封号实践（无被封先例 vs 公开风险）：https://goatcorp.github.io/faq/xl_troubleshooting.html
- NS2 登陆公告：https://na.finalfantasyxiv.com/lodestone/topics/detail/a6ea855ea930740e9f2f42380256b1f522b619b9
