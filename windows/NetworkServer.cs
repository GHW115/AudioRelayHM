// 网络服务（TCP 控制 + UDP 音频）
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AudioRelayWinUI;

public class NetworkServer {
    private TcpListener? listener; private TcpClient? client; private NetworkStream? stream;
    private UdpClient? udpSend; private UdpClient? udpRecv;
    private IPEndPoint? phoneEndPoint;
    private int seq;
    private readonly object netLock = new(); // 保护 udpSend/udpRecv/phoneEndPoint/client 跨线程访问
    private int _started; // StartAsync 防重入（快速重启时旧循环未退出）
    public bool Connected => client?.Connected ?? false;
    // TCP 监听是否已成功启动（listener.Start() 成功且未被 Stop）
    public bool Listening => listener != null;
    public event Action<AudioPacket>? OnAudioData;
    public event Action<bool>? OnConnected;
    public event Action<string>? OnLog;
    public event Action<EncodingType, int, int>? OnConfig;
    public event Action<int, int, int, int>? OnLatencyReport; // network, pcProcess, buffer, renderer
    private const int UDP_PORT = 9288;

    public async Task StartAsync(int port, CancellationToken tk) {
        // 防重入：快速重启时旧循环尚未退出，忽略本次启动，避免双循环踩踏共享字段
        if (Interlocked.Exchange(ref _started, 1) != 0) {
            OnLog?.Invoke("服务启动中/运行中，忽略重复启动");
            return;
        }
        try {
            // listener.Start() 在 try 内：端口被占用/无权限时在此抛出，避免成为未观察异常
            try {
                listener = new(IPAddress.Any, port);
                listener.Start();
            } catch (Exception ex) {
                OnLog?.Invoke($"启动失败: {ex.Message}");
                listener = null;
                return;
            }
            OnLog?.Invoke($"监听端口 {port} (TCP 控制 + UDP {UDP_PORT} 音频)");
            while (!tk.IsCancellationRequested) {
                try {
                    var newClient = await listener.AcceptTcpClientAsync(tk);
                    // 多客户端保护：新连接到来时断开旧连接并关闭旧 UDP socket（否则端口冲突）
                    lock (netLock) {
                        var oldClient = client;
                        client = newClient;
                        try { oldClient?.Close(); } catch { }
                        try { udpRecv?.Close(); } catch { }
                        try { udpSend?.Close(); } catch { }
                        udpRecv = null; udpSend = null; phoneEndPoint = null;
                    }
                    client.NoDelay = true;
                    stream = client.GetStream();

                    // 获取手机端 IP，用于 UDP 发送
                    var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                    lock (netLock) { phoneEndPoint = new IPEndPoint(remoteEp!.Address, UDP_PORT); }
                    OnLog?.Invoke($"手机端 IP: {remoteEp.Address}，UDP 目标端口 {UDP_PORT}");

                    // 启动 UDP 接收（双向音频）
                    lock (netLock) {
                        udpRecv = new UdpClient(UDP_PORT);
                        udpSend = new UdpClient();
                    }
                    _ = Task.Run(() => UdpReceiveLoop(tk), tk);

                OnConnected?.Invoke(true);

                // TCP 只处理控制消息（音频走 UDP）
                const int HEADER_SIZE = 44;
                var recvBuf = new byte[262144];
                int recvLen = 0;
                var readBuf = new byte[65536];

                while (client.Connected && !tk.IsCancellationRequested) {
                    int n = await stream.ReadAsync(readBuf, 0, readBuf.Length, tk);
                    if (n == 0) break;

                    if (recvLen + n > recvBuf.Length)
                        Array.Resize(ref recvBuf, (recvLen + n) * 2);
                    Array.Copy(readBuf, 0, recvBuf, recvLen, n);
                    recvLen += n;

                    int offset = 0;
                    while (recvLen - offset >= HEADER_SIZE) {
                        int payloadLen = BitConverter.ToInt32(recvBuf, offset + 40);
                        int totalLen = HEADER_SIZE + payloadLen;
                        if (recvLen - offset < totalLen) break;

                        var pktData = new byte[totalLen];
                        Array.Copy(recvBuf, offset, pktData, 0, totalLen);
                        var pkt = AudioPacket.Deserialize(pktData);

                        if (pkt.MsgType == MessageType.AudioData) OnAudioData?.Invoke(pkt);
                        else if (pkt.MsgType == MessageType.Control && pkt.Command == ControlCommand.Handshake) {
                            // 响应握手请求
                            OnLog?.Invoke("收到握手请求，发送 HANDSHAKE_ACK");
                            var ack = new AudioPacket {
                                MsgType = MessageType.Control, Command = ControlCommand.HandshakeAck,
                                Sequence = Interlocked.Increment(ref seq) - 1, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                SampleRate = 0, Channels = 0, BitsPerSample = 0, Payload = Array.Empty<byte>()
                            };
                            if (stream != null) await stream.WriteAsync(ack.Serialize());
                        }
                        else if (pkt.MsgType == MessageType.Control && pkt.Command == ControlCommand.Heartbeat) {
                            // 心跳：回应 HEARTBEAT 以维持连接双向活跃
                            var hb = new AudioPacket {
                                MsgType = MessageType.Control, Command = ControlCommand.Heartbeat,
                                Sequence = Interlocked.Increment(ref seq) - 1, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                SampleRate = 0, Channels = 0, BitsPerSample = 0, Payload = Array.Empty<byte>()
                            };
                            if (stream != null) await stream.WriteAsync(hb.Serialize());
                        }
                        else if (pkt.MsgType == MessageType.Control && pkt.Command == ControlCommand.Config && pkt.Payload.Length >= 9) {
                            var enc = (EncodingType)pkt.Payload[0];
                            int bitrate = BitConverter.ToInt32(pkt.Payload, 1);
                            int bufferMs = BitConverter.ToInt32(pkt.Payload, 5);
                            OnConfig?.Invoke(enc, bitrate, bufferMs);
                        }
                        else if (pkt.MsgType == MessageType.Control && pkt.Command == ControlCommand.LatencyReport && pkt.Payload.Length >= 16) {
                            int network = BitConverter.ToInt32(pkt.Payload, 0);
                            int pcProcess = BitConverter.ToInt32(pkt.Payload, 4);
                            int buffer = BitConverter.ToInt32(pkt.Payload, 8);
                            int renderer = BitConverter.ToInt32(pkt.Payload, 12);
                            OnLatencyReport?.Invoke(network, pcProcess, buffer, renderer);
                        }
                        else if (pkt.MsgType == MessageType.Control && pkt.Command == ControlCommand.LatencyReport && pkt.Payload.Length >= 12) {
                            int total = BitConverter.ToInt32(pkt.Payload, 0);
                            int net = BitConverter.ToInt32(pkt.Payload, 4);
                            int buf = BitConverter.ToInt32(pkt.Payload, 8);
                            OnLatencyReport?.Invoke(net, 0, buf, total - net - buf);
                        }
                        else if (pkt.MsgType == MessageType.Control && pkt.Command == ControlCommand.LatencyReport && pkt.Payload.Length >= 4) {
                            int latencyMs = BitConverter.ToInt32(pkt.Payload, 0);
                            OnLatencyReport?.Invoke(latencyMs, 0, 0, 0);
                        }
                        else if (pkt.MsgType == MessageType.Control && pkt.Command == ControlCommand.TimeSync && pkt.Payload.Length >= 8) {
                            long pcTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var reply = new byte[16];
                            Array.Copy(pkt.Payload, 0, reply, 0, 8);
                            BitConverter.GetBytes(pcTime).CopyTo(reply, 8);
                            var ack = new AudioPacket { MsgType = MessageType.Control, Command = ControlCommand.TimeSync,
                                Sequence = Interlocked.Increment(ref seq) - 1, Timestamp = pcTime, SampleRate = 0, Channels = 0, BitsPerSample = 0, Payload = reply };
                            if (stream != null) await stream.WriteAsync(ack.Serialize());
                        }
                        offset += totalLen;
                    }

                    if (offset > 0) {
                        int remaining = recvLen - offset;
                        if (remaining > 0) Array.Copy(recvBuf, offset, recvBuf, 0, remaining);
                        recvLen = remaining;
                    }
                }
            } catch (OperationCanceledException) { break; }
            catch (Exception ex) { OnLog?.Invoke($"连接异常: {ex.Message}"); }
            finally {
                OnConnected?.Invoke(false);
                lock (netLock) {
                    try { udpRecv?.Close(); } catch { }
                    try { udpSend?.Close(); } catch { }
                    udpRecv = null; udpSend = null; phoneEndPoint = null;
                }
                stream?.Close(); client?.Close();
            }
        }
        } finally {
            Interlocked.Exchange(ref _started, 0); // 允许下次启动
        }
    }

    private async Task UdpReceiveLoop(CancellationToken tk) {
        UdpClient? udp;
        lock (netLock) udp = udpRecv;
        if (udp == null) return;
        try {
            while (!tk.IsCancellationRequested) {
                var result = await udp.ReceiveAsync(tk);
                // 首次收到手机 UDP 包时更新 phoneEndPoint
                IPEndPoint? firstEp = null;
                lock (netLock) {
                    if (phoneEndPoint == null) { phoneEndPoint = result.RemoteEndPoint; firstEp = result.RemoteEndPoint; }
                }
                if (firstEp != null) OnLog?.Invoke($"UDP 手机端点已记录: {firstEp}");
                var pkt = AudioPacket.Deserialize(result.Buffer);
                if (pkt.MsgType == MessageType.AudioData) OnAudioData?.Invoke(pkt);
            }
        } catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { OnLog?.Invoke($"UDP 接收异常: {ex.Message}"); }
    }

    public async Task SendAudioAsync(byte[] data, int sr, byte ch, EncodingType enc = EncodingType.Pcm, long? captureTime = null, long? encodeTime = null) {
        UdpClient? udp; IPEndPoint? ep;
        lock (netLock) { udp = udpSend; ep = phoneEndPoint; }
        if (udp == null || ep == null) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var p = new AudioPacket { MsgType = MessageType.AudioData, Direction = StreamDirection.PcToPhone,
            Encoding = enc, Sequence = Interlocked.Increment(ref seq) - 1,
            Timestamp = captureTime ?? now,
            EncodeTimestamp = encodeTime ?? now,
            SendTimestamp = now,
            SampleRate = sr, Channels = ch, BitsPerSample = 16, Payload = data };
        var pktBytes = p.Serialize();
        await udp.SendAsync(pktBytes, pktBytes.Length, ep);
    }
    public void Stop() {
        try { udpRecv?.Close(); } catch { }
        try { udpSend?.Close(); } catch { }
        udpRecv = null; udpSend = null; phoneEndPoint = null;
        stream?.Close(); client?.Close();
        try { listener?.Stop(); } catch { }
        listener = null;
    }
}
