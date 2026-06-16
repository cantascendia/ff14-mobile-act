using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ff14Act.TheoryTest;

/// <summary>
/// 把 ActionEffect 投影成 FFXIV_ACT_Plugin 的「network log line」(type 21)。
/// 这是 overlay 与 FFLogs 共用的【单一事实源】(见 docs/design.md §3.1)。
///
/// 格式: 十进制LogLineType | ISO-8601±offset 时间戳 | 字段… | 行尾MD5hash
/// 行尾 hash 是 cactbot/ACT 对该行(不含 hash 部分)算的 MD5,用于证明行未被截断/篡改
/// (只证完整性,不证语义 —— 见 design.md §8 R5)。
/// </summary>
internal static class ActLogLine
{
    public const int NetworkAbilityType = 21;

    public static string ProjectActionEffect(in ActionEffect ae, DateTimeOffset ts)
    {
        // type 21 前导字段序(LogGuide): type|ts|casterId|caster|abilityId|ability|targetId|target|...
        // 此处给出语义关键的前导字段 + damage 对(flags+value),足以喂 overlay/FFLogs 解析。
        var sb = new StringBuilder();
        sb.Append(NetworkAbilityType.ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(ts.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(ae.CasterId.ToString("X8", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(ae.CasterName).Append('|');
        sb.Append(ae.AbilityId.ToString("X", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(ae.AbilityName).Append('|');
        sb.Append(ae.TargetId.ToString("X8", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(ae.TargetName).Append('|');
        // action-effect 对: flags(03=damage done) + damage value(hex)
        sb.Append("03").Append('|');
        sb.Append(ae.Damage.ToString("X", CultureInfo.InvariantCulture)).Append('|');

        return AppendHash(sb.ToString());
    }

    /// <summary>cactbot 行尾 hash: 对截至最后一个 '|' 的内容算 MD5 hex,再接到行尾。</summary>
    private static string AppendHash(string lineWithTrailingPipe)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(lineWithTrailingPipe))).ToLowerInvariant();
        return lineWithTrailingPipe + hash;
    }
}
