// 网络包协议（44 字节包头）
using System;
using System.IO;

namespace AudioRelayWinUI;

public enum MessageType : byte { Control = 0, AudioData = 1 }
public enum ControlCommand : byte { Handshake = 0, HandshakeAck = 1, Heartbeat = 2, StartStream = 3, StopStream = 4, Volume = 5, Config = 6, LatencyReport = 7, TimeSync = 8 }
public enum StreamDirection : byte { PcToPhone = 0, PhoneToPc = 1 }
public enum EncodingType : byte { Pcm = 0, Opus = 1, Adpcm = 2 }
public class AudioPacket {
    public MessageType MsgType; public ControlCommand? Command; public StreamDirection? Direction;
    public EncodingType Encoding;
    public int Sequence; public long Timestamp; public long EncodeTimestamp; public long SendTimestamp;
    public int SampleRate;
    public byte Channels, BitsPerSample; public byte[] Payload = [];

    public byte[] Serialize() {
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write((byte)MsgType); bw.Write((byte?)(byte?)Command ?? 0xFF);
        bw.Write((byte?)(byte?)Direction ?? 0xFF); bw.Write((byte)Encoding);
        bw.Write(Sequence); bw.Write(Timestamp); bw.Write(EncodeTimestamp); bw.Write(SendTimestamp);
        bw.Write(SampleRate); bw.Write(Channels); bw.Write(BitsPerSample); bw.Write((ushort)0);
        bw.Write(Payload.Length); if (Payload.Length > 0) bw.Write(Payload);
        return ms.ToArray();
    }
    public static AudioPacket Deserialize(byte[] d) {
        const int HeaderSize = 44;
        const int MaxPayload = 65536;
        if (d == null || d.Length < HeaderSize)
            throw new ArgumentException($"AudioPacket too short: {d?.Length ?? 0} < {HeaderSize}", nameof(d));
        using var ms = new MemoryStream(d); using var br = new BinaryReader(ms);
        var p = new AudioPacket { MsgType = (MessageType)br.ReadByte() };
        var c = br.ReadByte(); if (c != 0xFF) p.Command = (ControlCommand)c;
        var dr = br.ReadByte(); if (dr != 0xFF) p.Direction = (StreamDirection)dr;
        p.Encoding = (EncodingType)br.ReadByte();
        p.Sequence = br.ReadInt32(); p.Timestamp = br.ReadInt64(); p.EncodeTimestamp = br.ReadInt64(); p.SendTimestamp = br.ReadInt64();
        p.SampleRate = br.ReadInt32(); p.Channels = br.ReadByte(); p.BitsPerSample = br.ReadByte(); br.ReadUInt16();
        int len = br.ReadInt32();
        // 防御：负载长度来自网络（不可信），限制上限与剩余缓冲
        if (len < 0 || len > MaxPayload || len > d.Length - HeaderSize)
            throw new InvalidDataException($"Invalid payload length: {len}");
        if (len > 0) p.Payload = br.ReadBytes(len);
        return p;
    }
}
