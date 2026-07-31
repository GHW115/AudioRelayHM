using AudioRelayWinUI;
using Xunit;

namespace AudioRelayWinUI.Tests;

/// <summary>
/// AudioPacket 序列化/反序列化 测试
/// 覆盖：往返一致性、跨端金丝雀字节序列、边界值
/// </summary>
public class AudioPacketTests
{
    // ==================== 往返测试 ====================

    [Fact]
    public void Roundtrip_AudioData_Pcm()
    {
        var original = new AudioPacket
        {
            MsgType = MessageType.AudioData,
            Direction = StreamDirection.PcToPhone,
            Encoding = EncodingType.Pcm,
            Sequence = 42,
            Timestamp = 1000,
            EncodeTimestamp = 1001,
            SendTimestamp = 1002,
            SampleRate = 48000,
            Channels = 2,
            BitsPerSample = 16,
            Payload = new byte[] { 0x01, 0x02, 0x03, 0x04 }
        };

        var result = AudioPacket.Deserialize(original.Serialize());

        Assert.Equal(MessageType.AudioData, result.MsgType);
        Assert.Null(result.Command); // 音频包不应有 Command
        Assert.Equal(StreamDirection.PcToPhone, result.Direction);
        Assert.Equal(EncodingType.Pcm, result.Encoding);
        Assert.Equal(42, result.Sequence);
        Assert.Equal(1000, result.Timestamp);
        Assert.Equal(1001, result.EncodeTimestamp);
        Assert.Equal(1002, result.SendTimestamp);
        Assert.Equal(48000, result.SampleRate);
        Assert.Equal(2, (int)result.Channels);
        Assert.Equal(16, (int)result.BitsPerSample);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, result.Payload);
    }

    [Fact]
    public void Roundtrip_AudioData_Opus()
    {
        var payload = new byte[256];
        new Random(42).NextBytes(payload);

        var original = new AudioPacket
        {
            MsgType = MessageType.AudioData,
            Direction = StreamDirection.PhoneToPc,
            Encoding = EncodingType.Opus,
            Sequence = 999,
            Timestamp = long.MaxValue - 1,
            EncodeTimestamp = 0,
            SendTimestamp = long.MinValue + 1,
            SampleRate = 16000,
            Channels = 1,
            BitsPerSample = 16,
            Payload = payload
        };

        var result = AudioPacket.Deserialize(original.Serialize());

        Assert.Equal(MessageType.AudioData, result.MsgType);
        Assert.Equal(StreamDirection.PhoneToPc, result.Direction);
        Assert.Equal(EncodingType.Opus, result.Encoding);
        Assert.Equal(999, result.Sequence);
        Assert.Equal(long.MaxValue - 1, result.Timestamp);
        Assert.Equal(0, result.EncodeTimestamp);
        Assert.Equal(long.MinValue + 1, result.SendTimestamp);
        Assert.Equal(16000, result.SampleRate);
        Assert.Equal(1, (int)result.Channels);
        Assert.Equal(16, (int)result.BitsPerSample);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void Roundtrip_AudioData_Adpcm()
    {
        var original = new AudioPacket
        {
            MsgType = MessageType.AudioData,
            Direction = StreamDirection.PcToPhone,
            Encoding = EncodingType.Adpcm,
            Sequence = 1,
            Timestamp = 0,
            EncodeTimestamp = 0,
            SendTimestamp = 0,
            SampleRate = 48000,
            Channels = 2,
            BitsPerSample = 16,
            Payload = Array.Empty<byte>()
        };

        var result = AudioPacket.Deserialize(original.Serialize());

        Assert.Equal(EncodingType.Adpcm, result.Encoding);
        Assert.Empty(result.Payload);
    }

    [Fact]
    public void Roundtrip_EmptyPayload()
    {
        var original = new AudioPacket
        {
            MsgType = MessageType.AudioData,
            Direction = StreamDirection.PcToPhone,
            Encoding = EncodingType.Pcm,
            Sequence = 0,
            Timestamp = 0,
            EncodeTimestamp = 0,
            SendTimestamp = 0,
            SampleRate = 0,
            Channels = 0,
            BitsPerSample = 0,
            Payload = Array.Empty<byte>()
        };

        var result = AudioPacket.Deserialize(original.Serialize());

        Assert.Equal(0, result.SampleRate);
        Assert.Equal(0, (int)result.Channels);
        Assert.Empty(result.Payload);
    }

    [Fact]
    public void Roundtrip_ControlMessage_Config()
    {
        var original = new AudioPacket
        {
            MsgType = MessageType.Control,
            Command = ControlCommand.Config,
            Sequence = 100,
            Timestamp = 5000,
            Payload = new byte[] { 0x01, 0x40, 0x00, 0x00, 0x00, 0xC8, 0x00, 0x00, 0x00 }
        };

        var result = AudioPacket.Deserialize(original.Serialize());

        Assert.Equal(MessageType.Control, result.MsgType);
        Assert.Equal(ControlCommand.Config, result.Command);
        Assert.Null(result.Direction); // 控制包不应有 Direction
        Assert.Equal(100, result.Sequence);
        Assert.Equal(5000, result.Timestamp);
        Assert.Equal(new byte[] { 0x01, 0x40, 0x00, 0x00, 0x00, 0xC8, 0x00, 0x00, 0x00 }, result.Payload);
    }

    [Fact]
    public void Roundtrip_ControlMessage_TimeSync()
    {
        var original = new AudioPacket
        {
            MsgType = MessageType.Control,
            Command = ControlCommand.TimeSync,
            Sequence = 0,
            Timestamp = 0x1234567890ABCDEF,
            Payload = new byte[16]
        };

        var result = AudioPacket.Deserialize(original.Serialize());

        Assert.Equal(MessageType.Control, result.MsgType);
        Assert.Equal(ControlCommand.TimeSync, result.Command);
        Assert.Equal(0x1234567890ABCDEF, result.Timestamp);
        Assert.Equal(16, result.Payload.Length);
    }

    [Fact]
    public void Roundtrip_LargePayload()
    {
        var payload = new byte[65536];
        new Random(123).NextBytes(payload);

        var original = new AudioPacket
        {
            MsgType = MessageType.AudioData,
            Direction = StreamDirection.PcToPhone,
            Encoding = EncodingType.Pcm,
            Sequence = 1,
            Timestamp = 1,
            EncodeTimestamp = 1,
            SendTimestamp = 1,
            SampleRate = 48000,
            Channels = 2,
            BitsPerSample = 16,
            Payload = payload
        };

        var result = AudioPacket.Deserialize(original.Serialize());

        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void Roundtrip_Int64BoundaryValues()
    {
        // 测试 Int64 时间戳的端序正确性（边界值）
        long[] testValues = { 0, 1, -1, long.MaxValue, long.MinValue, 0x00FF_00FF_00FF_00FF };

        foreach (var ts in testValues)
        {
            var original = new AudioPacket
            {
                MsgType = MessageType.AudioData,
                Direction = StreamDirection.PhoneToPc,
                Encoding = EncodingType.Pcm,
                Sequence = 1,
                Timestamp = ts,
                EncodeTimestamp = ts + 1,
                SendTimestamp = ts + 2,
                SampleRate = 48000,
                Channels = 2,
                BitsPerSample = 16,
                Payload = Array.Empty<byte>()
            };

            var result = AudioPacket.Deserialize(original.Serialize());
            Assert.Equal(ts, result.Timestamp);
            Assert.Equal(ts + 1, result.EncodeTimestamp);
            Assert.Equal(ts + 2, result.SendTimestamp);
        }
    }

    // ==================== 金丝雀测试（跨端协议一致性） ====================
    // 这些已知字节序列必须与 ArkTS 端 AudioPacket.serialize() 输出完全一致

    /// <summary>
    /// 金丝雀 1: PCM AudioData 包（PC→Phone，48kHz/2ch/16bit，空 payload）
    /// </summary>
    [Fact]
    public void Canary_PcmAudioData_EmptyPayload()
    {
        // 手工构造已知字节序列（与 ArkTS DataView 输出对齐）
        var bytes = new byte[44];
        bytes[0] = 0x01;  // msgType = AUDIO_DATA
        bytes[1] = 0xFF;  // command = 0xFF (未设置)
        bytes[2] = 0x00;  // direction = PC_TO_PHONE
        bytes[3] = 0x00;  // encoding = PCM
        // sequence = 5 (int32 LE)
        bytes[4] = 0x05; bytes[5] = 0x00; bytes[6] = 0x00; bytes[7] = 0x00;
        // timestamp = 1000 (int64 LE)
        bytes[8]  = 0xE8; bytes[9]  = 0x03; bytes[10] = 0x00; bytes[11] = 0x00;
        bytes[12] = 0x00; bytes[13] = 0x00; bytes[14] = 0x00; bytes[15] = 0x00;
        // encodeTimestamp = 1001 (int64 LE)
        bytes[16] = 0xE9; bytes[17] = 0x03; bytes[18] = 0x00; bytes[19] = 0x00;
        bytes[20] = 0x00; bytes[21] = 0x00; bytes[22] = 0x00; bytes[23] = 0x00;
        // sendTimestamp = 1002 (int64 LE)
        bytes[24] = 0xEA; bytes[25] = 0x03; bytes[26] = 0x00; bytes[27] = 0x00;
        bytes[28] = 0x00; bytes[29] = 0x00; bytes[30] = 0x00; bytes[31] = 0x00;
        // sampleRate = 48000 (int32 LE)
        bytes[32] = 0x80; bytes[33] = 0xBB; bytes[34] = 0x00; bytes[35] = 0x00;
        // channels = 2
        bytes[36] = 0x02;
        // bitsPerSample = 16
        bytes[37] = 0x10;
        // reserved = 0 (2 bytes)
        bytes[38] = 0x00; bytes[39] = 0x00;
        // payloadLength = 0 (int32 LE)
        bytes[40] = 0x00; bytes[41] = 0x00; bytes[42] = 0x00; bytes[43] = 0x00;

        var packet = AudioPacket.Deserialize(bytes);

        Assert.Equal(MessageType.AudioData, packet.MsgType);
        Assert.Null(packet.Command);
        Assert.Equal(StreamDirection.PcToPhone, packet.Direction);
        Assert.Equal(EncodingType.Pcm, packet.Encoding);
        Assert.Equal(5, packet.Sequence);
        Assert.Equal(1000, packet.Timestamp);
        Assert.Equal(1001, packet.EncodeTimestamp);
        Assert.Equal(1002, packet.SendTimestamp);
        Assert.Equal(48000, packet.SampleRate);
        Assert.Equal(2, (int)packet.Channels);
        Assert.Equal(16, (int)packet.BitsPerSample);
        Assert.Empty(packet.Payload);
    }

    /// <summary>
    /// 金丝雀 2: CONFIG 控制包（Opus 64kbps, buffer 200ms）
    /// </summary>
    [Fact]
    public void Canary_ControlConfig_Opus64k_Buffer200ms()
    {
        // 构造已知字节序列
        var header = new byte[44];
        header[0] = 0x00;  // msgType = CONTROL
        header[1] = 0x06;  // command = CONFIG (6)
        header[2] = 0xFF;  // direction = 0xFF (未设置)
        header[3] = 0x01;  // encoding = Opus (无关控制包，但保留)
        // sequence = 7 (int32 LE)
        header[4] = 0x07; header[5] = 0x00; header[6] = 0x00; header[7] = 0x00;
        // timestamp = 2000 (int64 LE)
        header[8]  = 0xD0; header[9]  = 0x07; header[10] = 0x00; header[11] = 0x00;
        header[12] = 0x00; header[13] = 0x00; header[14] = 0x00; header[15] = 0x00;
        // encodeTimestamp = 0
        // sendTimestamp = 0
        // sampleRate = 0
        // channels = 0
        // bitsPerSample = 0
        // reserved = 0
        // payloadLength = 9 (int32 LE)
        header[40] = 0x09; header[41] = 0x00; header[42] = 0x00; header[43] = 0x00;

        // payload: [encoding=1(Opus)] [bitrate=64(int32 LE)] [bufferMs=200(int32 LE)]
        var payload = new byte[9];
        payload[0] = 0x01;  // encoding = Opus
        payload[1] = 0x40; payload[2] = 0x00; payload[3] = 0x00; payload[4] = 0x00;  // bitrate = 64
        payload[5] = 0xC8; payload[6] = 0x00; payload[7] = 0x00; payload[8] = 0x00;  // bufferMs = 200

        var full = new byte[53];
        Array.Copy(header, full, 44);
        Array.Copy(payload, 0, full, 44, 9);

        var packet = AudioPacket.Deserialize(full);

        Assert.Equal(MessageType.Control, packet.MsgType);
        Assert.Equal(ControlCommand.Config, packet.Command);
        Assert.Null(packet.Direction);
        Assert.Equal(7, packet.Sequence);
        Assert.Equal(2000, packet.Timestamp);
        Assert.Equal(9, packet.Payload.Length);
        Assert.Equal(0x01, packet.Payload[0]);  // encoding = Opus
    }

    /// <summary>
    /// 金丝雀 3: TIME_SYNC 控制包（16 字节 payload：8字节 phoneTime + 8字节 pcTime）
    /// </summary>
    [Fact]
    public void Canary_ControlTimeSync()
    {
        var header = new byte[44];
        header[0] = 0x00;  // msgType = CONTROL
        header[1] = 0x08;  // command = TIME_SYNC (8)
        header[2] = 0xFF;  // direction = 0xFF (未设置)
        header[3] = 0x00;  // encoding = PCM
        // sequence = 99 (int32 LE)
        header[4] = 0x63; header[5] = 0x00; header[6] = 0x00; header[7] = 0x00;
        // timestamp = 0x1122334455667788 (int64 LE)
        header[8]  = 0x88; header[9]  = 0x77; header[10] = 0x66; header[11] = 0x55;
        header[12] = 0x44; header[13] = 0x33; header[14] = 0x22; header[15] = 0x11;
        // payloadLength = 16 (int32 LE)
        header[40] = 0x10; header[41] = 0x00; header[42] = 0x00; header[43] = 0x00;

        var payload = new byte[16];
        // phoneTime 低32位 = 0xAABBCCDD, 高32位 = 0x00112233
        payload[0]  = 0xDD; payload[1]  = 0xCC; payload[2]  = 0xBB; payload[3]  = 0xAA;
        payload[4]  = 0x33; payload[5]  = 0x22; payload[6]  = 0x11; payload[7]  = 0x00;
        // pcTime 低32位 = 0x44556677, 高32位 = 0x00998877
        payload[8]  = 0x77; payload[9]  = 0x66; payload[10] = 0x55; payload[11] = 0x44;
        payload[12] = 0x77; payload[13] = 0x88; payload[14] = 0x99; payload[15] = 0x00;

        var full = new byte[60];
        Array.Copy(header, full, 44);
        Array.Copy(payload, 0, full, 44, 16);

        var packet = AudioPacket.Deserialize(full);

        Assert.Equal(MessageType.Control, packet.MsgType);
        Assert.Equal(ControlCommand.TimeSync, packet.Command);
        Assert.Equal(99, packet.Sequence);
        Assert.Equal(0x1122334455667788, packet.Timestamp);
        Assert.Equal(16, packet.Payload.Length);
    }

    /// <summary>
    /// 金丝雀 4: 序列化后的大小必须精确等于 44 + payloadLength
    /// </summary>
    [Fact]
    public void SerializedSize_Equals_44_Plus_PayloadLength()
    {
        for (int len = 0; len <= 1024; len += 256)
        {
            var packet = new AudioPacket
            {
                MsgType = MessageType.AudioData,
                Direction = StreamDirection.PcToPhone,
                Encoding = EncodingType.Pcm,
                Sequence = 1,
                Timestamp = 1,
                EncodeTimestamp = 1,
                SendTimestamp = 1,
                SampleRate = 48000,
                Channels = 2,
                BitsPerSample = 16,
                Payload = new byte[len]
            };

            byte[] serialized = packet.Serialize();
            Assert.Equal(44 + len, serialized.Length);
        }
    }

    // ==================== 输入防御测试（网络不可信数据） ====================

    [Fact]
    public void Deserialize_Null_Throws()
    {
        Assert.Throws<ArgumentException>(() => AudioPacket.Deserialize(null!));
    }

    [Fact]
    public void Deserialize_TooShort_Throws()
    {
        // 小于 44 字节包头
        Assert.Throws<ArgumentException>(() => AudioPacket.Deserialize(new byte[43]));
        Assert.Throws<ArgumentException>(() => AudioPacket.Deserialize(new byte[0]));
    }

    [Theory]
    [InlineData(-1)]   // 负数长度
    [InlineData(65537)] // 超过协议上限 64KB
    [InlineData(int.MaxValue)] // 畸形大长度
    public void Deserialize_InvalidPayloadLength_Throws(int maliciousLen)
    {
        var packet = new AudioPacket { MsgType = MessageType.AudioData, Payload = [] };
        var buf = packet.Serialize();
        // 篡改 payload 长度字段（偏移 40，小端序）
        BitConverter.GetBytes(maliciousLen).CopyTo(buf, 40);
        Assert.Throws<System.IO.InvalidDataException>(() => AudioPacket.Deserialize(buf));
    }

    [Fact]
    public void Deserialize_PayloadLengthExceedsBuffer_Throws()
    {
        // 头部声明 100 字节负载，但实际只有 44 字节
        var packet = new AudioPacket { MsgType = MessageType.AudioData, Payload = [] };
        var buf = packet.Serialize();
        BitConverter.GetBytes(100).CopyTo(buf, 40);
        Assert.Throws<System.IO.InvalidDataException>(() => AudioPacket.Deserialize(buf));
    }
}
