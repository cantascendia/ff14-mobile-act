// ════════════════════════════════════════════════════════════════════════════
//  Ff14Act.TheoryTest —— 「理论上手机能用 ACT 监测 PS5/NS2 的 FF14」端到端证明
// ════════════════════════════════════════════════════════════════════════════
//
//  把【不依赖专有二进制/硬件】的全部软件链路用合成数据端到端跑通并断言。
//  跑通 = 证明: 一旦拿到【解压后的 zone 包字节】(由 Machina+Oodle 在 PS5/NS2 流上产出),
//  后续「去混淆 → 解析 ActionEffect → 投影 ACT 日志行 → 经 OverlayPlugin WS 喂给
//  手机/浏览器 overlay」整条链在软件层成立 —— 且这整条链都是可跑在 .NET(for Android)
//  ARM64 的纯 C#。
//
//  仅剩未由本测试覆盖的链路(已在 docs 记录为硬件闸门,非软件问题):
//    (G3) Oodle Network 原生解压【对真实 FF14 流】—— x64 由 Machina 实证,
//         ARM64 需取 UE 分发的 .so 再过版本匹配(runbook §0/§4)。
//    (物理) 手机物理傍受 PS5 流量 —— bind 53 / tethered 需 root(README 已订正)。
//
//  退出码 = 失败的检查数(0 = 全绿 = 理论被测试证实)。

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ff14Act.TheoryTest;

var runner = new TestRunner();

// 贯穿全测的合成输入(seed 来自网线 init 包;tables 每补丁离线提取 —— 此处合成占位)
var tables = Deobfuscation.Tables.Synthetic();
var seed = new Deobfuscation.Seed(Mode: 7, Seed1: 0x12345678, Seed2: 0xABCD, Seed3: 0xCAFEBABE);

// 一条「服务器权威」的战斗事件(我们要一路把它无损送达 overlay)
var truth = new ActionEffect(
    CasterId: 0x10001234, CasterName: "Y'shtola",
    AbilityId: 0x1D60, AbilityName: "Fire IV",
    TargetId: 0x40000ABC, TargetName: "Striking Dummy",
    Damage: 13520);

// ── 检查 1: 去混淆的【网络可重建性】+【可逆性】(项目 make-or-break) ──────────
runner.Check("1. 去混淆: rand 相消 ⇒ 仅凭网络 seed+表即可重建密钥(无需读内存)", () =>
{
    ushort opcode = ActionEffectCodec.ActionEffectOpcode;

    // 客户端内存里混入了【观测不到】的 localRand / packetRand
    uint localRand = 0x0BADF00D, packetRand = 0xFEEDFACE;
    uint clientKey = Deobfuscation.ClientKeyToUse(seed, opcode, tables, localRand, packetRand);

    // 观测者只有网络输入(seed/tables/opcode),没有任何 rand
    uint observerKey = Deobfuscation.ObserverKeyToUse(seed, opcode, tables);

    Assert.True(clientKey == observerKey,
        $"密钥不一致 client=0x{clientKey:X8} observer=0x{observerKey:X8}(rand 未相消则网络-only 解码不成立)");

    // 可逆性: 客户端用 clientKey 混淆,观测者用重建的 observerKey 还原
    var payload = Encoding.UTF8.GetBytes("the quick brown chocobo jumps over 13520 damage");
    var obf = Deobfuscation.Obfuscate(payload, clientKey);
    Assert.True(!obf.AsSpan().SequenceEqual(payload), "混淆后与明文相同(未真正混淆)");
    var recovered = Deobfuscation.Deobfuscate(obf, observerKey);
    Assert.True(recovered.AsSpan().SequenceEqual(payload), "去混淆未还原出原文");
});

// ── 检查 2: 全链路 解压后字节 → 去混淆 → 解析 ActionEffect 字段无损 ───────────
runner.Check("2. ActionEffect: header 明文/body 混淆 → 观测者去混淆 → 字段精确还原", () =>
{
    // 客户端: 序列化 IPC,opcode header 留明文(线上可读),仅混淆 body
    var ipc = ActionEffectCodec.Serialize(truth);
    ushort opcodeOnWire = ActionEffectCodec.PeekOpcode(ipc);
    uint clientKey = Deobfuscation.ClientKeyToUse(seed, opcodeOnWire, tables, 0x11111111, 0x22222222);

    var wire = (byte[])ipc.Clone();
    var obfBody = Deobfuscation.Obfuscate(ipc.AsSpan(2).ToArray(), clientKey);
    obfBody.CopyTo(wire.AsSpan(2));
    Assert.True(!wire.AsSpan(2).SequenceEqual(ipc.AsSpan(2)), "body 未被混淆");

    // 观测者: 从明文 header 读 opcode → 重建密钥 → 去混淆 body → 解析
    ushort op = ActionEffectCodec.PeekOpcode(wire);
    Assert.True(ActionEffectCodec.IsActionEffect(op), $"opcode 0x{op:X4} 非 ActionEffect");
    uint observerKey = Deobfuscation.ObserverKeyToUse(seed, op, tables);
    var plainBody = Deobfuscation.Deobfuscate(wire.AsSpan(2).ToArray(), observerKey);
    var recon = (byte[])wire.Clone();
    plainBody.CopyTo(recon.AsSpan(2));

    var parsed = ActionEffectCodec.Parse(recon);
    Assert.True(parsed == truth, $"解析字段不符: 期望 {truth} 实得 {parsed}");
});

// ── 检查 3: 投影成 FFXIV_ACT_Plugin type 21 日志行(overlay+FFLogs 的单一事实源) ──
runner.Check("3. 日志行: 投影 type 21 网络日志行 + 行尾 MD5(cactbot 格式)", () =>
{
    var ts = new DateTimeOffset(2026, 6, 16, 21, 30, 15, TimeSpan.FromHours(9));
    var line = ActLogLine.ProjectActionEffect(truth, ts);
    var parts = line.Split('|');

    Assert.True(parts[0] == "21", "LogLineType 非 21");
    Assert.True(DateTimeOffset.TryParse(parts[1], out _), "时间戳非合法 ISO-8601");
    Assert.True(parts[2] == truth.CasterId.ToString("X8"), "casterId 字段错");
    Assert.True(parts[3] == truth.CasterName, "caster 名字段错");
    Assert.True(parts[4] == truth.AbilityId.ToString("X"), "abilityId 字段错");
    Assert.True(parts[6] == truth.TargetId.ToString("X8"), "targetId 字段错");
    Assert.True(parts[9] == truth.Damage.ToString("X"), "damage 字段错");

    var hash = parts[^1];
    Assert.True(hash.Length == 32 && hash.All(Uri.IsHexDigit), "行尾 hash 非 32 位 hex");
    var content = line[..(line.LastIndexOf('|') + 1)];
    var expect = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    Assert.True(expect == hash, "行尾 MD5 不匹配(完整性校验失败)");

    Console.WriteLine("      ↳ " + line);
});

// ── 检查 4: OverlayPlugin WS —— 真实 ClientWebSocket 连上并收到 LogLine+CombatData ──
await runner.CheckAsync("4. OverlayPlugin WS: 手机端(ClientWebSocket)连接→subscribe→收实时事件", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var ct = timeout.Token;

    await using var server = new OverlayWsServer();   // 临时端口(生产为 10501)
    server.Start();
    Console.WriteLine($"      ↳ overlay WS 监听 ws://127.0.0.1:{server.Port}/ws (生产: ws://<手机IP>:10501/ws)");

    using var client = new System.Net.WebSockets.ClientWebSocket();
    await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), ct);

    // 手机端 overlay 的标准握手
    var subscribe = """{"call":"subscribe","events":["LogLine","CombatData"]}""";
    await client.SendAsync(Encoding.UTF8.GetBytes(subscribe),
        System.Net.WebSockets.WebSocketMessageType.Text, true, ct);

    // 等服务端登记该连接
    while (server.ClientCount == 0) await Task.Delay(20, ct);

    // 服务端把检查 3 的日志行 + 一帧聚合 DPS 推给所有 overlay
    var rawLine = ActLogLine.ProjectActionEffect(truth, DateTimeOffset.UtcNow);
    var logLineMsg = JsonSerializer.Serialize(new
    {
        type = "broadcast",
        msgtype = "LogLine",
        msg = new { line = new[] { "21" }, rawLine }
    });
    var combatDataMsg = JsonSerializer.Serialize(new
    {
        type = "broadcast",
        msgtype = "CombatData",
        msg = new
        {
            Encounter = new { title = "Striking Dummy", duration = "00:30", DPS = "450.7" },
            Combatant = new Dictionary<string, object>
            {
                ["Y'shtola"] = new { name = "Y'shtola", job = "BLM", dps = "450.7", damage = "13520" }
            }
        }
    });

    await server.BroadcastAsync(logLineMsg, ct);
    await server.BroadcastAsync(combatDataMsg, ct);

    // 手机端收到两条事件并解出 msgtype
    var seen = new HashSet<string>();
    for (var i = 0; i < 2; i++)
    {
        var json = await ReceiveTextAsync(client, ct);
        using var doc = JsonDocument.Parse(json);
        seen.Add(doc.RootElement.GetProperty("msgtype").GetString()!);
    }

    Assert.True(seen.Contains("LogLine"), "未收到 LogLine 事件");
    Assert.True(seen.Contains("CombatData"), "未收到 CombatData 事件");
    Console.WriteLine($"      ↳ 手机端收到事件: {string.Join(", ", seen)}");

    await client.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", ct);
});

return runner.Summarize();


static async Task<string> ReceiveTextAsync(System.Net.WebSockets.ClientWebSocket ws, CancellationToken ct)
{
    var buf = new byte[16384];
    var sb = new StringBuilder();
    System.Net.WebSockets.WebSocketReceiveResult res;
    do
    {
        res = await ws.ReceiveAsync(buf, ct);
        sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
    } while (!res.EndOfMessage);
    return sb.ToString();
}


// ── 极简测试运行器 ──────────────────────────────────────────────────────────
internal sealed class TestRunner
{
    private int _failed;

    public void Check(string name, Action body)
    {
        try { body(); Pass(name); }
        catch (Exception ex) { Fail(name, ex); }
    }

    public async Task CheckAsync(string name, Func<Task> body)
    {
        try { await body(); Pass(name); }
        catch (Exception ex) { Fail(name, ex); }
    }

    private void Pass(string name) => Console.WriteLine($"[PASS] {name}");

    private void Fail(string name, Exception ex)
    {
        _failed++;
        Console.WriteLine($"[FAIL] {name}\n       {ex.Message}");
    }

    public int Summarize()
    {
        Console.WriteLine(new string('─', 70));
        if (_failed == 0)
            Console.WriteLine("✅ 全部通过 —— 软件链路理论成立。剩余仅 Oodle-ARM64(G3) 与物理傍受(root) 两个硬件闸门。");
        else
            Console.WriteLine($"❌ {_failed} 项失败。");
        return _failed;
    }
}

internal static class Assert
{
    public static void True(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }
}
