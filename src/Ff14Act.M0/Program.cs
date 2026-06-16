// M0 生死证明 harness —— 离线把一段 PS5 FFXIV zone pcap 解出至少一个 ActionEffect。
//
// 用法:
//   Ff14Act.M0.exe --pcap capture.pcapng --oodle <含 oodle-network-shared.dll 的目录> --tables <去混淆六表目录>
//
// 诚实声明: 本文件未经编译验证(开发机无 .NET SDK)。它是【结构正确的足场】,
// 标注了每个上游(Machina/Unscrambler/FFXIVOpcodes)精确的接入点。把每个 STAGE 的
// TODO 按对应上游 API 填上即可。失败会停在 docs/m0-runbook.md §4 的某个闸门。

using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Ff14Act.M0;

internal static class Program
{
    private static int Main(string[] args)
    {
        var opt = CliOptions.Parse(args);
        if (opt is null) { CliOptions.PrintUsage(); return 2; }

        Console.WriteLine($"[M0] pcap={opt.PcapPath} oodle={opt.OodleDir} tables={opt.TablesDir}");

        // ── STAGE ①：读 pcap，按 TCP 连接重组载荷 ─────────────────────────────
        // 选出 FFXIV zone 流。识别启发式：与 zone 服务器(EnterWorld 下发的 IP)的
        // 长连接、双向、首包后持续。M0 阶段可先打印所有 TCP 流让你肉眼挑出 zone 流。
        var connections = ReadTcpStreams(opt.PcapPath);

        foreach (var conn in connections)
        {
            Console.WriteLine($"[stage1] connection {conn.Key} bytes={conn.PayloadLength}");

            // ── STAGE ②③：bundle 解帧 + Oodle 解压 ──────────────────────────
            // 用 Machina：
            //   - Machina.FFXIV.Headers / FFXIVBundleDecoder 做 bundle 解帧
            //   - Machina.FFXIV.Oodle.OodleTCPWrapper(空窗口 0x100000, 空训练) 解压
            //     需 oodle-network-shared.dll 在 opt.OodleDir(或 PATH/工作目录)。
            // 关键: Oodle 状态【必须从该连接首包起】逐包喂入,否则永久错位(闸门 G3)。
            //
            // TODO(②③): 把 conn 的有序载荷逐包喂进 Machina 的解码器,拿到解压后的
            //            IPC 消息流(7.2+ 仍是混淆态)。
            IEnumerable<byte[]> ipcMessages = DecompressWithMachina(conn, opt.OodleDir);

            foreach (var ipc in ipcMessages)
            {
                // ── STAGE ④：去混淆 (7.2+) ──────────────────────────────────
                // 移植 Unscrambler:
                //   - 从初始化包(7.4: 专用 init 包,社区记 opcode 702)读 mode/seed1/seed2/seed3
                //   - KeyGenerator7x.GenerateFromUnknownInitializer() 派生 key
                //   - 用 opt.TablesDir 下的六表(table0/1/2 / midtable / daytable / opcodekeytable)
                //     做 per-opcode XOR+减法去混淆。seed 需在连接内前向携带。
                //
                // TODO(④): byte[] plain = Deobfuscator.Apply(ipc, seedState, tables);
                byte[] plain = Deobfuscate(ipc, opt.TablesDir);

                // ── STAGE ⑤：opcode 匹配 ActionEffect ──────────────────────
                // 用 FFXIVOpcodes 当前补丁(Global)表里的
                //   Ability1/8/16/24/32 (= ActionEffect 各目标变体) opcode。
                //
                // TODO(⑤): if (OpcodeMap.IsActionEffect(opcode)) { 解析并打印 }
                if (TryParseActionEffect(plain, out var ae))
                {
                    Console.WriteLine($"[M0 ✅ PASS] ActionEffect: caster={ae.CasterName}({ae.CasterId:X}) " +
                                      $"ability={ae.AbilityName}({ae.AbilityId}) target={ae.TargetId:X} damage={ae.Damage}");
                    return 0; // 解出一个就够了 —— M0 通过。
                }
            }
        }

        Console.Error.WriteLine("[M0 ✗] 未解出 ActionEffect。对照 docs/m0-runbook.md §4 定位闸门(G2~G5)。");
        return 1;
    }

    // ── STAGE ① 实现：纯 pcap → 按连接聚合 TCP 载荷(可独立验证,不依赖任何上游) ──
    private static IReadOnlyList<TcpStream> ReadTcpStreams(string pcapPath)
    {
        var streams = new Dictionary<string, TcpStream>();
        using var device = new CaptureFileReaderDevice(pcapPath);
        device.Open();

        PacketCapture e;
        while (device.GetNextPacket(out e) == GetPacketStatus.PacketRead)
        {
            var raw = e.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            if (packet.Extract<TcpPacket>() is not { } tcp) continue;
            if (packet.Extract<IPPacket>() is not { } ip) continue;
            if (tcp.PayloadData.Length == 0) continue;

            var key = $"{ip.SourceAddress}:{tcp.SourcePort}->{ip.DestinationAddress}:{tcp.DestinationPort}";
            if (!streams.TryGetValue(key, out var s))
                streams[key] = s = new TcpStream(key);
            s.Append(tcp.SequenceNumber, tcp.PayloadData);
        }
        return streams.Values.ToList();
    }

    // ── 下面三个是【上游接入桩】。M0 的真正工作量在这里,按 runbook §1 填实。 ──
    private static IEnumerable<byte[]> DecompressWithMachina(TcpStream conn, string oodleDir)
    {
        // TODO(②③): 接 Machina 的 FFXIVBundleDecoder + OodleTCPWrapper。
        throw new NotImplementedException("STAGE ②③: 接 Machina bundle 解帧 + Oodle 解压 (runbook §3 / 闸门 G3)");
    }

    private static byte[] Deobfuscate(byte[] ipc, string tablesDir)
    {
        // TODO(④): 移植 Unscrambler KeyGenerator7x + 六表。
        throw new NotImplementedException("STAGE ④: 去混淆 (runbook §3 / 闸门 G4)");
    }

    private static bool TryParseActionEffect(byte[] plain, out ActionEffect ae)
    {
        // TODO(⑤): 用 FFXIVOpcodes 判 opcode + 按 ActionEffect 结构解字段。
        ae = default;
        return false;
    }
}

internal readonly record struct ActionEffect(
    uint CasterId, string CasterName, uint AbilityId, string AbilityName, uint TargetId, uint Damage);
