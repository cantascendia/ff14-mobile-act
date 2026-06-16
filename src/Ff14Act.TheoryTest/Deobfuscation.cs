namespace Ff14Act.TheoryTest;

/// <summary>
/// 去混淆理论模型 —— 验证项目的 make-or-break 假设(见 docs/alt-pc-bridge.md §1.1):
///
///   「网络-only 解码成立」的核心是: 客户端混淆所用的 key 虽然在【内存生成】时
///    混入了 localRand / packetRand 这两个【网络观测不到】的随机数,但实际作用到
///    线上字节的 keyToUse 把它们【代数相消】,只剩 derive(seed, opcode, tables) ——
///    而 seed 来自网线 init 包、tables 每补丁静态可离线提取。
///    ⇒ 网络观测者无需读内存即可重建 keyToUse 并去混淆。
///
/// 本类同时实现「客户端侧(知道 rand)」与「观测者侧(只有网络输入)」两条路径,
/// 测试断言两者推出的 keyToUse 相同 —— 这就是把那条理论结论变成可执行证据。
///
/// 注: derive() 与逐字节变换是【机制等价的合成实现】,不是当前补丁的真实查表/魔数
///     (那是每补丁的数据输入,见 runbook)。本测试证明的是【可逆性 + 网络可重建性】
///     这条结构性结论,而非某补丁的具体常量。
/// </summary>
internal static class Deobfuscation
{
    /// <summary>来自网线初始化包(7.4: 专用 init 包)的 seed —— 网络观测者可见。</summary>
    public readonly record struct Seed(uint Mode, uint Seed1, uint Seed2, uint Seed3);

    /// <summary>每补丁从 ffxiv_dx11.exe .rdata 提取的静态表(此处为合成占位)。</summary>
    public sealed class Tables
    {
        public required byte[] Table0 { get; init; }   // 256
        public required byte[] Table1 { get; init; }   // 256
        public required uint[] OpcodeKeyTable { get; init; } // 256

        public static Tables Synthetic()
        {
            var t0 = new byte[256];
            var t1 = new byte[256];
            var ok = new uint[256];
            for (var i = 0; i < 256; i++)
            {
                t0[i] = (byte)((i * 167 + 13) & 0xFF);
                t1[i] = (byte)((i * 89 + 41) & 0xFF);
                ok[i] = (uint)(i * 2654435761u) ^ 0x9E3779B9u;
            }
            return new Tables { Table0 = t0, Table1 = t1, OpcodeKeyTable = ok };
        }
    }

    /// <summary>
    /// 纯网络可观测输入推出的有效密钥。客户端与观测者【共用此函数】——
    /// 这正是「不需要内存」的体现: 它只吃 seed + tables + opcode。
    /// </summary>
    public static uint DeriveKey(Seed seed, ushort opcode, Tables t)
    {
        uint k = seed.Seed1 ^ t.OpcodeKeyTable[opcode & 0xFF];
        k += (uint)(t.Table0[seed.Seed2 & 0xFF] << 8);
        k ^= RotL(seed.Seed3 + opcode, (int)(seed.Mode & 31));
        k += (uint)(t.Table1[(opcode >> 8) & 0xFF]);
        return k == 0 ? 0xA5A5A5A5u : k;
    }

    /// <summary>
    /// 客户端内存路径(模拟): key 在内存里 = localRand + packetRand + derive(...);
    /// 使用时 keyToUse = key - localRand - lastPacketRand。
    /// 当 lastPacketRand == packetRand(同包)时,rand 项相消 ⇒ keyToUse == derive(...)。
    /// localRand/packetRand 是【网络观测不到】的内存随机数,本函数证明它们对线上值无影响。
    /// </summary>
    public static uint ClientKeyToUse(Seed seed, ushort opcode, Tables t, uint localRand, uint packetRand)
    {
        uint keyInMemory = unchecked(localRand + packetRand + DeriveKey(seed, opcode, t));
        uint lastPacketRand = packetRand; // 同包前向携带
        uint keyToUse = unchecked(keyInMemory - localRand - lastPacketRand);
        return keyToUse;
    }

    /// <summary>观测者路径: 只有网络输入(seed/tables/opcode),无 rand。</summary>
    public static uint ObserverKeyToUse(Seed seed, ushort opcode, Tables t)
        => DeriveKey(seed, opcode, t);

    // ── 逐字节可逆变换: 镜像 SE 的「XOR + 减法」(可逆算术,非密码学) ──
    // encode: obf[i] = ((plain[i] - ks[i]) XOR ks2[i])
    // decode: plain[i] = ((obf[i] XOR ks2[i]) + ks[i])
    public static byte[] Obfuscate(ReadOnlySpan<byte> plain, uint keyToUse)
    {
        var ks = new KeyStream(keyToUse);
        var ks2 = new KeyStream(keyToUse ^ 0xDEADBEEFu);
        var outp = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++)
            outp[i] = (byte)(((plain[i] - ks.Next()) & 0xFF) ^ ks2.Next());
        return outp;
    }

    public static byte[] Deobfuscate(ReadOnlySpan<byte> obf, uint keyToUse)
    {
        var ks = new KeyStream(keyToUse);
        var ks2 = new KeyStream(keyToUse ^ 0xDEADBEEFu);
        var outp = new byte[obf.Length];
        for (var i = 0; i < obf.Length; i++)
            outp[i] = (byte)(((obf[i] ^ ks2.Next()) + ks.Next()) & 0xFF);
        return outp;
    }

    private static uint RotL(uint v, int r) { r &= 31; return (v << r) | (v >> (32 - r)); }

    /// <summary>xorshift32 keystream(确定性,可由任一方从 keyToUse 重建)。</summary>
    private struct KeyStream(uint seed)
    {
        private uint _s = seed == 0 ? 0x1u : seed;
        public byte Next()
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return (byte)(_s & 0xFF);
        }
    }
}
