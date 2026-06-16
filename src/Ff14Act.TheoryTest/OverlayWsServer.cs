using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Ff14Act.TheoryTest;

/// <summary>
/// 最小的 OverlayPlugin 兼容 WebSocket 服务端(见 docs/design.md §3.2)。
/// 这是「手机/浏览器连上手机托管的 overlay 看实时 ACT」那条显示链路。
///
/// 实现要点: 手动完成 HTTP Upgrade 握手(算 Sec-WebSocket-Accept),再把 NetworkStream
/// 交给 BCL 的 WebSocket.CreateFromStream 处理 RFC6455 帧 —— 全程仅 BCL,无 HttpListener
/// URL ACL/管理员权限问题,绑高位 loopback 端口普通用户即可。
/// 生产里端点是 ws://&lt;手机IP&gt;:10501/ws;测试用临时端口避免端口冲突。
/// </summary>
internal sealed class OverlayWsServer : IAsyncDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private readonly TcpListener _listener;
    private readonly List<WebSocket> _clients = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public OverlayWsServer(int requestedPort = 0)
        => _listener = new TcpListener(IPAddress.Loopback, requestedPort);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ClientCount
    {
        get { lock (_lock) return _clients.Count(w => w.State == WebSocketState.Open); }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public async Task BroadcastAsync(string json, CancellationToken ct = default)
    {
        WebSocket[] snapshot;
        lock (_lock) snapshot = _clients.Where(w => w.State == WebSocketState.Open).ToArray();
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var ws in snapshot)
        {
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
            catch { /* drop dead client silently */ }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(tcp, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        using var _ = tcp;
        var stream = tcp.GetStream();

        var key = await ReadHandshakeKeyAsync(stream, ct);
        if (key is null) return;
        await WriteHandshakeResponseAsync(stream, key, ct);

        var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(30));
        lock (_lock) _clients.Add(ws);

        // OverlayPlugin 客户端会发 {"call":"subscribe",...};测试只需把入站消息排空。
        var buf = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { lock (_lock) _clients.Remove(ws); }
    }

    private static async Task<string?> ReadHandshakeKeyAsync(NetworkStream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!EndsWithDoubleCrlf(sb))
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0) return null;
            sb.Append((char)one[0]);
            if (sb.Length > 16384) return null;
        }
        foreach (var line in sb.ToString().Split("\r\n"))
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                return line["Sec-WebSocket-Key:".Length..].Trim();
        return null;
    }

    private static bool EndsWithDoubleCrlf(StringBuilder sb)
    {
        var n = sb.Length;
        return n >= 4 && sb[n - 4] == '\r' && sb[n - 3] == '\n' && sb[n - 2] == '\r' && sb[n - 1] == '\n';
    }

    private static async Task WriteHandshakeResponseAsync(NetworkStream s, string key, CancellationToken ct)
    {
        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + WsGuid)));
        var resp =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(resp);
        await s.WriteAsync(bytes, ct);
        await s.FlushAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { }
        }
        _cts?.Dispose();
    }
}
