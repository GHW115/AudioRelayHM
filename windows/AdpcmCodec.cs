// IMA ADPCM 编解码器
using System;

namespace AudioRelayWinUI;

// ========== ADPCM 编解码器 ==========
public static class AdpcmCodec
{
    private static readonly int[] StepTable = {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
        253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
        3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
        12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    };

    private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

    /// <summary>编码 PCM short[] 为 ADPCM byte[]（每声道独立编码，4-bit 交错打包）</summary>
    public static byte[] Encode(short[] pcm, int channels) => Encode(pcm, channels, pcm.Length);
    public static byte[] Encode(short[] pcm, int channels, int sampleCount)
    {
        int totalSamples = sampleCount;
        int encodedLen = (totalSamples + 1) / 2; // 每2个样本→1字节
        byte[] output = new byte[encodedLen + channels * 8]; // 前 ch*8 字节存初始状态

        // 保存每个声道的初始预测值和步长索引
        int[] predictors = new int[channels];
        int[] indices = new int[channels];
        for (int ch = 0; ch < channels; ch++)
        {
            indices[ch] = 0;
            predictors[ch] = 0;
        }

        int sampleIdx = 0;
        int outIdx = channels * 8;
        int frames = totalSamples / channels;

        for (int f = 0; f < frames; f++)
        {
            for (int ch = 0; ch < channels; ch += 2)
            {
                int code0 = EncodeSample(pcm[sampleIdx], ref predictors[ch], ref indices[ch]);
                int code1 = (ch + 1 < channels)
                    ? EncodeSample(pcm[sampleIdx + 1], ref predictors[ch + 1], ref indices[ch + 1])
                    : 0;
                output[outIdx++] = (byte)(code0 | (code1 << 4));
                sampleIdx += Math.Min(2, channels);
            }
        }

        // 把预测器和步长索引存到开头
        for (int ch = 0; ch < channels; ch++)
        {
            BitConverter.GetBytes(predictors[ch]).CopyTo(output, ch * 4);
            BitConverter.GetBytes(indices[ch]).CopyTo(output, channels * 4 + ch * 4);
        }

        return output;
    }

    private static int EncodeSample(int sample, ref int predictor, ref int index)
    {
        int diff = sample - predictor;
        int code = 0;
        int step = StepTable[Math.Clamp(index, 0, 88)];

        int absDiff = Math.Abs(diff);
        if (diff < 0) code = 8;

        int delta = step >> 3;
        int tmp = step;
        if (absDiff >= tmp) { code |= 4; absDiff -= tmp; delta += tmp; }
        tmp >>= 1;
        if (absDiff >= tmp) { code |= 2; absDiff -= tmp; delta += tmp; }
        tmp >>= 1;
        if (absDiff >= tmp) { code |= 1; delta += tmp; }

        if ((code & 8) != 0)
            predictor -= delta;
        else
            predictor += delta;

        predictor = Math.Clamp(predictor, -32768, 32767);
        index = Math.Clamp(index + IndexTable[code & 7], 0, 88);

        return code & 0x0F;
    }

    /// <summary>解码 ADPCM byte[] 为 PCM short[]</summary>
    public static byte[] Decode(byte[] adpcm, byte channels, int sampleRate)
    {
        // 从开头读取初始状态
        int[] predictors = new int[channels];
        int[] indices = new int[channels];
        for (int ch = 0; ch < channels; ch++)
            predictors[ch] = BitConverter.ToInt32(adpcm, ch * 4);
        for (int ch = 0; ch < channels; ch++)
            indices[ch] = BitConverter.ToInt32(adpcm, channels * 4 + ch * 4);

        int dataOffset = channels * 8;
        int dataLen = adpcm.Length - dataOffset;
        int sampleCount = dataLen * 2; // 每字节2个样本
        short[] output = new short[sampleCount];

        int inIdx = dataOffset;
        int outIdx = 0;

        while (inIdx < adpcm.Length)
        {
            for (int ch = 0; ch < channels; ch += 2)
            {
                byte b = adpcm[inIdx++];
                int code0 = b & 0x0F;
                int code1 = (b >> 4) & 0x0F;

                output[outIdx++] = DecodeSample(code0, ref predictors[ch], ref indices[ch]);
                if (ch + 1 < channels)
                    output[outIdx++] = DecodeSample(code1, ref predictors[ch + 1], ref indices[ch + 1]);
            }
        }

        // 转 byte[]
        byte[] pcmBytes = new byte[output.Length * 2];
        Buffer.BlockCopy(output, 0, pcmBytes, 0, pcmBytes.Length);
        return pcmBytes;
    }

    private static short DecodeSample(int code, ref int predictor, ref int index)
    {
        int step = StepTable[Math.Clamp(index, 0, 88)];
        int delta = step >> 3;

        if ((code & 4) != 0) delta += step;
        if ((code & 2) != 0) delta += step >> 1;
        if ((code & 1) != 0) delta += step >> 2;

        if ((code & 8) != 0)
            predictor -= delta;
        else
            predictor += delta;

        predictor = Math.Clamp(predictor, -32768, 32767);
        index = Math.Clamp(index + IndexTable[code & 7], 0, 88);

        return (short)predictor;
    }
}
