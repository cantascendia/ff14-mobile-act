namespace Ff14Act.M0;

/// <summary>
/// 单条 TCP 连接的有序载荷重组。按 sequence number 排序后拼接,
/// 给下游 Oodle「从首包起、按序、零丢包」的输入(有状态解压的硬前提)。
/// 注意: 这是 M0 离线回放的简化重组(假定 pcap 已完整无丢)。实时/串接版需处理
/// 重传、乱序、分片——那是 Machina 的 TCP 重组职责,M0 阶段可先信任 pcap。
/// </summary>
internal sealed class TcpStream
{
    private readonly List<(uint Seq, byte[] Data)> _segments = new();

    public TcpStream(string key) => Key = key;

    public string Key { get; }

    public int PayloadLength => _segments.Sum(s => s.Data.Length);

    public void Append(uint seq, byte[] data) => _segments.Add((seq, data));

    /// <summary>按 seq 排序、去重后的连续字节流。</summary>
    public IReadOnlyList<(uint Seq, byte[] Data)> OrderedSegments =>
        _segments.OrderBy(s => s.Seq).ToList();
}
