using System.Buffers.Binary;

namespace Ff14Act.TheoryTest;

/// <summary>
/// 解码出的 ActionEffect(对应 ACT 日志行 type 21 NetworkAbility)的语义模型。
/// </summary>
internal readonly record struct ActionEffect(
    uint CasterId, string CasterName,
    uint AbilityId, string AbilityName,
    uint TargetId, string TargetName,
    uint Damage);

/// <summary>
/// 合成 IPC 编解码 —— 用一个【自洽】的二进制布局序列化/反序列化 ActionEffect。
/// 真实补丁的字节偏移由 FFXIVOpcodes/struct 定义提供(每补丁数据输入);本测试验证的是
/// 「解压→去混淆后,能按结构正确解出语义字段」这条解析逻辑,而非某补丁的具体偏移。
///
/// 布局(小端):
///   [0..2)  opcode (ushort)
///   [2..6)  casterId (uint)
///   [6..10) targetId (uint)
///   [10..14) abilityId (uint)
///   [14..18) damage (uint)
///   [18..50) casterName (utf8, 32B 定长,0 填充)
///   [50..82) abilityName
///   [82..114) targetName
/// </summary>
internal static class ActionEffectCodec
{
    public const ushort ActionEffectOpcode = 0x0315; // 合成 opcode(真实值见 FFXIVOpcodes 当前补丁)
    private const int NameLen = 32;
    public const int Size = 18 + NameLen * 3;

    public static byte[] Serialize(in ActionEffect ae)
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), ActionEffectOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), ae.CasterId);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), ae.TargetId);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), ae.AbilityId);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(14), ae.Damage);
        WriteName(b.AsSpan(18, NameLen), ae.CasterName);
        WriteName(b.AsSpan(50, NameLen), ae.AbilityName);
        WriteName(b.AsSpan(82, NameLen), ae.TargetName);
        return b;
    }

    public static ushort PeekOpcode(ReadOnlySpan<byte> ipc)
        => BinaryPrimitives.ReadUInt16LittleEndian(ipc);

    public static bool IsActionEffect(ushort opcode) => opcode == ActionEffectOpcode;

    public static ActionEffect Parse(ReadOnlySpan<byte> ipc)
    {
        if (ipc.Length < Size) throw new ArgumentException("IPC too short for ActionEffect");
        return new ActionEffect(
            CasterId: BinaryPrimitives.ReadUInt32LittleEndian(ipc.Slice(2)),
            CasterName: ReadName(ipc.Slice(18, NameLen)),
            AbilityId: BinaryPrimitives.ReadUInt32LittleEndian(ipc.Slice(10)),
            AbilityName: ReadName(ipc.Slice(50, NameLen)),
            TargetId: BinaryPrimitives.ReadUInt32LittleEndian(ipc.Slice(6)),
            TargetName: ReadName(ipc.Slice(82, NameLen)),
            Damage: BinaryPrimitives.ReadUInt32LittleEndian(ipc.Slice(14)));
    }

    private static void WriteName(Span<byte> dst, string name)
    {
        dst.Clear();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        bytes.AsSpan(0, Math.Min(bytes.Length, dst.Length)).CopyTo(dst);
    }

    private static string ReadName(ReadOnlySpan<byte> src)
    {
        var end = src.IndexOf((byte)0);
        if (end < 0) end = src.Length;
        return System.Text.Encoding.UTF8.GetString(src.Slice(0, end));
    }
}
