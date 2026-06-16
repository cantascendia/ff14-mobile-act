# M0 实战 Runbook —— 「PS5 的 FFXIV 流到底能不能在 PC 上解出来」

> 目的：把 [README](../README.md) 路线图里抽象的 **M0（生死证明）** 变成你一个下午能亲手跑完的实验。
> 跑通它 = 整个项目（PC版 + 手机版）的命门被验证；跑不通 = 后面全部免谈，且你只花了一个下午。
>
> **范围澄清（关键）**：M0 **不碰服务器、不发非法包、不利用任何服务器漏洞**。它只是把「你的 PS5 客户端收到的、发给你自己的」字节流，在你自己的 PC 上被动解压/去混淆——和 ACT / IINACT / Machina 做的事同类。唯一的非技术风险是 **SE 的 ToS** 与 **Oodle 库再分发**（仅在你对外分发时才触发），与「服务器安全」无关。
>
> **为什么先在 PC 上做**：M0 对 PC版 和 手机版**完全相同**（都要先回答「这条流能不能解」）。PC 全栈原生（C#/.NET，零移植），所以用 PC 验证最便宜。手机版的两个硬阻塞（`bind 53` 需 root、ARM64 Oodle）只有在 M0 通过、且你确定「非要手机单机不可」时才需要碰。

---

## 0. 你必须自己准备的三样东西（我无法替你提供）

| # | 东西 | 从哪来 | 为什么必须你来 |
|---|---|---|---|
| 1 | **一段从「角色选择画面之前」就开始抓的 PS5 zone 流量 pcap** | 你的 PS5 + 抓包（见 §2） | Oodle 有状态：**必须从 TCP 首包起**，中途丢一个包整条流报废 |
| 2 | **`oodle-network-shared.dll`（x64）** | UE GitHub 分发包内的 `msvc.zip`，或你已有的授权来源 | 闭源 RAD/Epic 库，**不可由本仓库再分发** |
| 3 | **当前补丁的去混淆六表 + opcode 表** | 自己跑 `Unscrambler.DataGenerator` 从 `ffxiv_dx11.exe` 提取；opcode 取自 `FFXIVOpcodes` | 每补丁变，且提取需你本地有游戏 exe |

> 三样齐了，M0 才能跑。缺任何一样，会在下面对应的「闸门」处明确失败——这正是 M0 的价值：**失败点是可诊断的，不是玄学。**

---

## 1. 上游依赖（克隆 / 引用）

```powershell
# 在 D:\projects\ff14-mobile-act\external 下
git clone https://github.com/ravahn/machina.git           # Oodle 解压 + TCP 重组 + bundle 解帧
git clone https://github.com/perchbirdd/Unscrambler.git    # 去混淆 KeyGenerator7x + 六表提取
git clone https://github.com/karashiiro/FFXIVOpcodes.git   # opcode→语义（含 Global 分支）
git clone https://github.com/NotNite/TemporalStasis.git    # 仅 PC 版做主动改写时才需要；M0(被动)可不用
```

- **M0 被动解码不需要 TemporalStasis**（它是主动 inline 代理，用于改写 EnterWorld，那是「真·串接/手机」阶段的事）。M0 只做「抓→解」，所以核心是 **Machina + Unscrambler + FFXIVOpcodes** 三件。
- Machina 是否有现成 NuGet 包随版本变化——优先用 **git submodule / 项目引用** 锁定一个你验证过的 commit，别依赖飘忽的包版本（去混淆这条赛道每补丁碎，锁版本是纪律）。

---

## 2. 抓包（满足 Oodle「从首包起、零丢包」）

M0 用**离线 pcap 回放**而不是实时，因为可复现、可反复调试、不怕调代码时漏包。

1. PC 装 **Npcap**（WinPcap API 兼容模式）。
2. 让 PS5 流量**物理穿过 PC**，二选一（这一步是为了 PC 能看到 PS5 的裸 TCP）：
   - **L2 网桥**（推荐）：PS5 网线接 PC 第二网口，PC 上把两个网卡桥接。主机仍拿路由器 IP，不引入 NAT，不破坏匹配。抓桥接网卡。
   - **移动热点 / ICS**：PS5 连 PC 的 WiFi 热点。零硬件但**双 NAT**，需先测副本匹配是否正常。
3. **先开 Wireshark/dumpcap 抓，再开 PS5 登录游戏**——确保抓到 zone TCP 连接的 SYN 和第一个数据包。
4. 进一场有输出的战斗（练习木桩即可），停止抓包，存 `capture.pcapng`。

> 闸门 G2：如果你抓不到、或抓包起点晚于 PS5 连接 → Oodle 必然解不出。先解决这步再往下。

---

## 3. 解码流水线（M0 代码做什么）

```
capture.pcapng
   │  ① 读 pcap，按 (src,dst,port) 选出 FFXIV zone TCP 流
   ▼
TCP 重组（按 connection.ID 各自独立）        ← Machina 的 FFXIVBundleDecoder
   │  ② 还原 FFXIV bundle 帧
   ▼
Oodle Network TCP 解压（空窗口/空训练，1MB）  ← Machina.FFXIV/Oodle + oodle-network-shared.dll
   │  ③ 得到「解压后但仍混淆」的 IPC 消息（7.2+）
   ▼
去混淆（取 init 包 seed → KeyGenerator7x → 六表 → per-opcode key）  ← 移植 Unscrambler
   │  ④ 得到明文 IPC
   ▼
opcode 匹配 ActionEffect(01/08/16/24/32)    ← FFXIVOpcodes 当前补丁表
   │  ⑤ 解出 caster / ability / target / damage
   ▼
打印一条 ActionEffect → ✅ M0 通过
```

代码足场见 [`src/Ff14Act.M0/`](../src/Ff14Act.M0/)。

---

## 4. 决策闸门（每个失败点意味着什么）

| 闸门 | 检查 | 失败的含义 |
|---|---|---|
| **G1 三样准备** | Oodle dll / 六表 / pcap 齐 | 缺啥补啥，非技术阻塞 |
| **G2 抓包完整** | 抓到 zone TCP SYN + 首包 | 抓包方法不对，换 L2 网桥 |
| **G3 Oodle 解压出非空字节** | 解压后长度合理、非乱码 | Oodle **版本与 FFXIV 不匹配**（最可能的硬伤），或没从首包起 |
| **G4 去混淆出合法结构** | 字段范围、actorId 合理 | 六表/seed 偏移过期，重新提取当前补丁 |
| **G5 解出 ActionEffect** | 打出 caster/ability/damage | **M0 通过 = 全项目命门验证** |

> **G3 是最关键的未知数**：RAD 官方说 Oodle 支持 Android/ARM64 与 x64，但「**与 FFXIV 当前补丁有状态 TCP 线格式字节级互换**」未经公开验证。G3 通过 = 消掉项目最大技术不确定性。

---

## 5. M0 通过之后

- 你**当场就已经有一个可用的「PC版个人自用 ACT」雏形**了——没碰手机、没碰 root、没碰 ARM64 Oodle。直接把它喂给 M1（投影成日志行 + 本地 overlay）。
- **只有**当你确定「非手机单机不可」时，才去碰手机版的两个硬阻塞：
  1. `bind UDP/53` → stock Android 几乎确定需 root（见 [README 已知硬限制](../README.md) 与上游分析）；把「无 root」看板改成「需 root / Magisk 模块」。
  2. ARM64 Oodle → 从 UE 分发包取 Android `.so`，再过一遍 G3 的版本匹配验证。

---

## 6. 诚实边界

- 本 runbook 的代码足场（[`src/Ff14Act.M0/`](../src/Ff14Act.M0/)）**未经我编译验证**（本机无 .NET SDK，且 M0 需你的硬件+Oodle+实流量才能端到端跑）。它是结构正确的起点，不是保证可运行的成品。
- 去混淆（步骤④）是**每补丁会碎的单点**，依赖 `Unscrambler` 上游（巴士因子≈1）。M0 只需解一个包，但长期运营这是结构性维护成本。
