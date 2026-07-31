using AudioRelayWinUI;
using Xunit;

namespace AudioRelayWinUI.Tests;

/// <summary>
/// 音频重采样测试：AudioCaptureService.ResampleToStereo48
/// 覆盖：同采样率直通、降采样、升采样、单声道扩展、环绕声下混
/// </summary>
public class AudioResampleTests
{
    // ==================== 同采样率直通 ====================

    /// <summary>
    /// 48kHz 立体声 → 48kHz 立体声：应保持零开销短路（值不变）
    /// </summary>
    [Fact]
    public void Resample_SameRate_Stereo_Identity()
    {
        int srcRate = 48000;
        int frames = 480; // 10ms
        int channels = 2;
        var input = new float[frames * channels];

        // 生成可识别的信号
        for (int i = 0; i < frames; i++)
        {
            input[i * 2] = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * i / srcRate);
            input[i * 2 + 1] = -0.3f;
        }

        var result = AudioCaptureService.ResampleToStereo48(input, srcRate, 48000, frames, channels);

        // 输出应为 48kHz 立体声
        Assert.Equal(frames * 2, result.Length);

        // 值应接近原始（线性插值在整数倍位置精确）
        for (int i = 0; i < frames; i++)
        {
            Assert.Equal(input[i * 2], result[i * 2], 0.001f);
            Assert.Equal(input[i * 2 + 1], result[i * 2 + 1], 0.001f);
        }
    }

    /// <summary>
    /// 48kHz 立体声 → 48kHz 立体声：零输入得零输出
    /// </summary>
    [Fact]
    public void Resample_SameRate_Silence()
    {
        int frames = 240;
        var input = new float[frames * 2]; // 全零立体声

        var result = AudioCaptureService.ResampleToStereo48(input, 48000, 48000, frames, 2);

        Assert.All(result, v => Assert.Equal(0, v, 0.001f));
    }

    // ==================== 降采样 ====================

    /// <summary>
    /// 96kHz 立体声 → 48kHz 立体声（2:1 降采样）
    /// </summary>
    [Fact]
    public void Resample_96kTo48k_Stereo()
    {
        int srcRate = 96000;
        int dstRate = 48000;
        int srcFrames = 960; // 10ms @ 96kHz
        int channels = 2;
        var input = new float[srcFrames * channels];

        // 生成 1kHz 正弦波
        for (int i = 0; i < srcFrames; i++)
        {
            float val = (float)Math.Sin(2 * Math.PI * 1000 * i / srcRate);
            input[i * 2] = val;
            input[i * 2 + 1] = val;
        }

        var result = AudioCaptureService.ResampleToStereo48(input, srcRate, dstRate, srcFrames, channels);

        // 预期帧数 = srcFrames * dstRate / srcRate = 960 * 48000 / 96000 = 480
        int expectedFrames = 480;
        Assert.Equal(expectedFrames * 2, result.Length);

        // 结果应为有效音频（非全零）
        Assert.Contains(result, v => Math.Abs(v) > 0.001f);
    }

    /// <summary>
    /// 44.1kHz 立体声 → 48kHz 立体声（非整数比升采样）
    /// </summary>
    [Fact]
    public void Resample_441kTo48k_Stereo()
    {
        int srcRate = 44100;
        int dstRate = 48000;
        int srcFrames = 441; // 10ms @ 44.1kHz
        int channels = 2;
        var input = new float[srcFrames * channels];

        for (int i = 0; i < srcFrames; i++)
        {
            float val = (float)Math.Sin(2 * Math.PI * 440 * i / srcRate);
            input[i * 2] = val;
            input[i * 2 + 1] = val;
        }

        var result = AudioCaptureService.ResampleToStereo48(input, srcRate, dstRate, srcFrames, channels);

        // 预期帧数 = (long)441 * 48000 / 44100 = 480
        int expectedFrames = (int)((long)srcFrames * dstRate / srcRate);
        Assert.Equal(expectedFrames * 2, result.Length);

        // 左声道和右声道应有值且接近
        for (int i = 0; i < expectedFrames; i++)
        {
            float diff = Math.Abs(result[i * 2] - result[i * 2 + 1]);
            Assert.True(diff < 0.01f, $"声道偏差 @ 帧{i}: L={result[i * 2]:F4} R={result[i * 2 + 1]:F4}");
        }
    }

    // ==================== 声道转换 ====================

    /// <summary>
    /// 单声道 → 立体声（左右声道应相同）
    /// </summary>
    [Fact]
    public void Resample_MonoToStereo()
    {
        int srcRate = 48000;
        int frames = 480;
        int channels = 1;
        var input = new float[frames];

        for (int i = 0; i < frames; i++)
            input[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / srcRate);

        var result = AudioCaptureService.ResampleToStereo48(input, srcRate, 48000, frames, channels);

        Assert.Equal(frames * 2, result.Length);

        // 左右声道应完全相同
        for (int i = 0; i < frames; i++)
        {
            Assert.Equal(result[i * 2], result[i * 2 + 1], 0.001f);
        }
    }

    /// <summary>
    /// 5.1 环绕声 → 立体声（仅取前两个声道 FL/FR）
    /// </summary>
    [Fact]
    public void Resample_SurroundToStereo()
    {
        int srcRate = 48000;
        int frames = 100;
        int channels = 6; // 5.1
        var input = new float[frames * channels];

        // FL=正弦, FR=余弦, 其余声道填充噪声
        var rng = new Random(42);
        for (int i = 0; i < frames; i++)
        {
            input[i * 6 + 0] = (float)Math.Sin(2 * Math.PI * i / 50);    // FL
            input[i * 6 + 1] = (float)Math.Cos(2 * Math.PI * i / 50);    // FR
            input[i * 6 + 2] = (float)(rng.NextDouble() - 0.5);           // C
            input[i * 6 + 3] = (float)(rng.NextDouble() - 0.5) * 0.1f;    // LFE
            input[i * 6 + 4] = (float)(rng.NextDouble() - 0.5);           // SL
            input[i * 6 + 5] = (float)(rng.NextDouble() - 0.5);           // SR
        }

        var result = AudioCaptureService.ResampleToStereo48(input, srcRate, 48000, frames, channels);

        Assert.Equal(frames * 2, result.Length);

        // 输出左声道应接近原始 FL，右声道应接近原始 FR
        for (int i = 0; i < frames; i++)
        {
            Assert.Equal(input[i * 6 + 0], result[i * 2], 0.001f);
            Assert.Equal(input[i * 6 + 1], result[i * 2 + 1], 0.001f);
        }
    }

    // ==================== 边界测试 ====================

    /// <summary>
    /// 空输入
    /// </summary>
    [Fact]
    public void Resample_ZeroFrames_ReturnsEmpty()
    {
        var input = Array.Empty<float>();
        var result = AudioCaptureService.ResampleToStereo48(input, 48000, 48000, 0, 2);
        Assert.Empty(result);
    }

    /// <summary>
    /// 单帧输入
    /// </summary>
    [Fact]
    public void Resample_SingleFrame()
    {
        int srcRate = 48000;
        var input = new float[] { 0.5f, -0.5f }; // 1帧立体声
        var result = AudioCaptureService.ResampleToStereo48(input, srcRate, 48000, 1, 2);

        Assert.Equal(2, result.Length);
        Assert.Equal(0.5f, result[0], 0.001f);
        Assert.Equal(-0.5f, result[1], 0.001f);
    }

    /// <summary>
    /// 极低采样率（8kHz）→ 48kHz
    /// </summary>
    [Fact]
    public void Resample_8kTo48k()
    {
        int srcRate = 8000;
        int dstRate = 48000;
        int srcFrames = 80; // 10ms @ 8kHz
        int channels = 1;
        var input = new float[srcFrames];
        for (int i = 0; i < srcFrames; i++)
            input[i] = (float)Math.Sin(2 * Math.PI * 440 * i / srcRate);

        var result = AudioCaptureService.ResampleToStereo48(input, srcRate, dstRate, srcFrames, channels);

        int expectedFrames = (int)((long)srcFrames * dstRate / srcRate); // 80*48000/8000 = 480
        Assert.Equal(expectedFrames * 2, result.Length);
        Assert.Contains(result, v => Math.Abs(v) > 0.001f);
    }

    /// <summary>
    /// 192kHz → 48kHz（4:1 降采样）
    /// </summary>
    [Fact]
    public void Resample_192kTo48k()
    {
        int srcRate = 192000;
        int dstRate = 48000;
        int srcFrames = 1920;
        var input = new float[srcFrames * 2];
        for (int i = 0; i < srcFrames; i++)
        {
            float val = (float)Math.Sin(2 * Math.PI * 1000 * i / srcRate);
            input[i * 2] = val;
            input[i * 2 + 1] = val;
        }

        var result = AudioCaptureService.ResampleToStereo48(input, srcRate, dstRate, srcFrames, 2);

        int expectedFrames = (int)((long)srcFrames * dstRate / srcRate); // 480
        Assert.Equal(expectedFrames * 2, result.Length);
    }

    /// <summary>
    /// 钳位测试：输入超 ±1.0 不应导致 NaN/Infinity
    /// </summary>
    [Fact]
    public void Resample_ClampedValues_NoNan()
    {
        int frames = 100;
        var input = new float[frames * 2];
        for (int i = 0; i < input.Length; i++)
            input[i] = (i % 3 == 0) ? 100f : -100f; // 远超正常范围

        var result = AudioCaptureService.ResampleToStereo48(input, 48000, 48000, frames, 2);

        Assert.All(result, v =>
        {
            Assert.False(float.IsNaN(v), "输出含 NaN");
            Assert.False(float.IsInfinity(v), "输出含 Infinity");
        });
    }
}
