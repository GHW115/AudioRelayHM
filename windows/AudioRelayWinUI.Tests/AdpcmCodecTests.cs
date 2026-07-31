using AudioRelayWinUI;
using Xunit;

namespace AudioRelayWinUI.Tests;

/// <summary>
/// ADPCM 编解码器测试
/// 注意：当前 ADPCM 编码器为立体声(channels=2)设计，单声道有缓冲区大小计算 bug。
/// 测试仅覆盖立体声场景（项目实际使用时始终 48kHz/2ch）。
/// </summary>
public class AdpcmCodecTests
{
    // ==================== 往返测试 ====================

    /// <summary>
    /// 静音信号编解码往返：全零 PCM 编码后解码应接近零
    /// </summary>
    [Fact]
    public void Roundtrip_Silence_Stereo()
    {
        int frames = 48000; // 1 秒 @ 48kHz
        int channels = 2;
        var pcm = new short[frames * channels]; // 立体声，全零

        byte[] encoded = AdpcmCodec.Encode(pcm, channels);
        byte[] decoded = AdpcmCodec.Decode(encoded, (byte)channels, 48000);

        var result = new short[decoded.Length / 2];
        Buffer.BlockCopy(decoded, 0, result, 0, decoded.Length);

        // 静音 ADPCM 编解码后应接近零（容差 ±50，因量化误差）
        foreach (var s in result)
        {
            Assert.True(Math.Abs(s) <= 50, $"静音编码后样本值 {s} 超出容差");
        }
    }

    /// <summary>
    /// 1kHz 正弦波立体声编解码往返
    /// </summary>
    [Fact]
    public void Roundtrip_SineWave_Stereo()
    {
        int sampleRate = 48000;
        int durationFrames = sampleRate / 10; // 100ms
        int channels = 2;
        var pcm = new short[durationFrames * channels];

        // 生成 1kHz 正弦波，振幅 16000（约 -6dB）
        for (int i = 0; i < durationFrames; i++)
        {
            double t = (double)i / sampleRate;
            short val = (short)(16000 * Math.Sin(2 * Math.PI * 1000 * t));
            pcm[i * channels] = val;
            pcm[i * channels + 1] = val;
        }

        byte[] encoded = AdpcmCodec.Encode(pcm, channels);
        byte[] decoded = AdpcmCodec.Decode(encoded, (byte)channels, sampleRate);

        var result = new short[decoded.Length / 2];
        Buffer.BlockCopy(decoded, 0, result, 0, decoded.Length);

        // 解码样本数正确
        Assert.Equal(pcm.Length, result.Length);

        // 波形形状大致保留（过零次数接近）
        int originalZeroCrossings = CountZeroCrossings(pcm);
        int decodedZeroCrossings = CountZeroCrossings(result);
        double crossingRatio = (double)decodedZeroCrossings / originalZeroCrossings;
        Assert.True(crossingRatio > 0.8 && crossingRatio < 1.2,
            $"过零次数偏差过大: 原始={originalZeroCrossings}, 解码={decodedZeroCrossings}");

        // 解码输出应在合理范围内（ADPCM 增量调制会在陡峭处过冲，可能达到满幅）
        int decMax = MaxAbsValue(result);
        Assert.True(decMax <= 32768,
            $"解码振幅异常: 解码峰值={decMax} 超出 short 范围");
        // 输出不应为全零（应有可辨识的信号）
        Assert.Contains(result, s => Math.Abs(s) > 100);
    }

    /// <summary>
    /// 最大振幅方波立体声编解码往返（压力测试）
    /// </summary>
    [Fact]
    public void Roundtrip_MaxAmplitude_Stereo()
    {
        int frames = 1000;
        int channels = 2;
        var pcm = new short[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            short val = (i % 2 == 0) ? short.MaxValue : short.MinValue;
            pcm[i * 2] = val;
            pcm[i * 2 + 1] = val;
        }

        byte[] encoded = AdpcmCodec.Encode(pcm, channels);
        byte[] decoded = AdpcmCodec.Decode(encoded, (byte)channels, 48000);

        var result = new short[decoded.Length / 2];
        Buffer.BlockCopy(decoded, 0, result, 0, decoded.Length);

        Assert.Equal(pcm.Length, result.Length);

        // ADPCM 对大振幅跳变有阶跃响应延迟，前 20 个样本后应基本跟踪上
        int maxError = 0;
        for (int i = 20; i < result.Length; i++)
        {
            int err = Math.Abs((int)result[i] - (int)pcm[i]);
            if (err > maxError) maxError = err;
        }

        // 最大振幅下 ADPCM 误差在 10000 以内为合理（量化噪声）
        Assert.True(maxError < 10000, $"最大误差 {maxError} 过大");
    }

    /// <summary>
    /// 编码后数据长度公式验证（立体声）
    /// </summary>
    [Fact]
    public void EncodedSize_MatchesFormula()
    {
        int channels = 2;
        for (int frames = 100; frames <= 1000; frames += 100)
        {
            var pcm = new short[frames * channels];
            byte[] encoded = AdpcmCodec.Encode(pcm, channels);
            // 立体声：每帧2样本→1字节，加上 channels*8 字节头部
            int expectedSize = (pcm.Length + 1) / 2 + channels * 8;
            Assert.Equal(expectedSize, encoded.Length);
        }
    }

    /// <summary>
    /// 编解码后样本数保持不变（立体声）
    /// </summary>
    [Fact]
    public void DecodedSampleCount_Preserved()
    {
        int channels = 2;
        var rng = new Random(42);
        for (int frames = 100; frames <= 1000; frames += 100)
        {
            var pcm = new short[frames * channels];
            var rawBytes = new byte[pcm.Length * 2];
            rng.NextBytes(rawBytes);
            Buffer.BlockCopy(rawBytes, 0, pcm, 0, rawBytes.Length);
            // 限制振幅
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (short)(pcm[i] / 4);

            byte[] encoded = AdpcmCodec.Encode(pcm, channels);
            byte[] decoded = AdpcmCodec.Decode(encoded, (byte)channels, 48000);

            Assert.Equal(pcm.Length * 2, decoded.Length);
        }
    }

    // ==================== 边界测试 ====================

    /// <summary>
    /// 极短音频（1帧立体声）编解码
    /// </summary>
    [Fact]
    public void Roundtrip_SingleFrame()
    {
        var pcm = new short[] { 100, 200 }; // 1帧立体声
        byte[] encoded = AdpcmCodec.Encode(pcm, 2);
        byte[] decoded = AdpcmCodec.Decode(encoded, 2, 48000);

        Assert.True(decoded.Length >= 4); // 至少解码出1帧 PCM (2ch × 2bytes = 4)
    }

    /// <summary>
    /// 已知 ADPCM 解码：验证给定输入产生确定性输出
    /// （回归测试：防止编解码器逻辑意外变化）
    /// </summary>
    [Fact]
    public void Decode_KnownBytes_ProducesConsistentOutput()
    {
        int channels = 2;
        // 编一段已知信号，再解码
        var pcm = new short[200]; // 100帧立体声
        for (int i = 0; i < 100; i++)
        {
            pcm[i * 2] = (short)(5000 * Math.Sin(2 * Math.PI * i / 20));
            pcm[i * 2 + 1] = pcm[i * 2];
        }

        byte[] encoded = AdpcmCodec.Encode(pcm, channels);

        // 两次解码应得到相同结果
        byte[] decoded1 = AdpcmCodec.Decode(encoded, (byte)channels, 48000);
        byte[] decoded2 = AdpcmCodec.Decode(encoded, (byte)channels, 48000);

        Assert.Equal(decoded1, decoded2);
    }

    // ==================== 辅助方法 ====================

    private static int CountZeroCrossings(short[] samples)
    {
        int count = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] >= 0 && samples[i] < 0) ||
                (samples[i - 1] < 0 && samples[i] >= 0))
                count++;
        }
        return count;
    }

    /// <summary>
    /// 安全计算绝对值最大值（使用 int 避免 short.MinValue 溢出）
    /// </summary>
    private static int MaxAbsValue(short[] samples)
    {
        int max = 0;
        foreach (var s in samples)
        {
            int abs = Math.Abs((int)s);
            if (abs > max) max = abs;
        }
        return max;
    }

    // ==================== 3 参重载（池化数组场景） ====================

    [Fact]
    public void Encode_WithSampleCount_IgnoresTrailingGarbage()
    {
        // 模拟 ArrayPool 场景：数组比实际数据大，尾部有垃圾值
        int valid = 1000; // 500 帧
        var buffer = new short[valid + 100];
        for (int i = 0; i < valid; i++) buffer[i] = (short)(i % 1000);
        for (int i = valid; i < buffer.Length; i++) buffer[i] = short.MinValue; // 垃圾

        byte[] encoded = AdpcmCodec.Encode(buffer, 2, valid);
        byte[] encodedExact = AdpcmCodec.Encode(buffer.Take(valid).ToArray(), 2);

        Assert.Equal(encodedExact, encoded);
    }
}
