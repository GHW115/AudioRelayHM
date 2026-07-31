/**
 * 🤖 AI 辅助生成 — DeepSeek V4
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/AdpcmDecoder.ets
// IMA ADPCM 解码器（ArkTS 层，供测试页面/未来解码需求使用）

export class AdpcmDecoder {
  private static readonly STEP_TABLE: number[] = [
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
    34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
    157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
    724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
    3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
  ];

  private static readonly INDEX_TABLE: number[] = [
    -1, -1, -1, -1, 2, 4, 6, 8,
    -1, -1, -1, -1, 2, 4, 6, 8
  ];

  static decode(adpcm: ArrayBuffer, channels: number): ArrayBuffer {
    let view = new DataView(adpcm);
    let offset = 0;
    let predictors: number[] = [];
    let indices: number[] = [];
    for (let ch = 0; ch < channels; ch++) {
      predictors.push(view.getInt32(offset, true));
      offset += 4;
    }
    for (let ch = 0; ch < channels; ch++) {
      indices.push(view.getInt32(offset, true));
      offset += 4;
    }
    let dataLen = adpcm.byteLength - offset;
    let sampleCount = dataLen * 2;
    let outBuf = new ArrayBuffer(sampleCount * 2);
    let outView = new DataView(outBuf);
    let outIdx = 0;
    while (offset < adpcm.byteLength) {
      let b = view.getUint8(offset);
      offset++;
      for (let ch = 0; ch < channels; ch += 2) {
        let code0 = b & 0x0F;
        let code1 = (b >> 4) & 0x0F;
        let sample0 = AdpcmDecoder.decodeSample(code0, predictors, indices, ch);
        outView.setInt16(outIdx, sample0, true);
        outIdx += 2;
        if (ch + 1 < channels) {
          let sample1 = AdpcmDecoder.decodeSample(code1, predictors, indices, ch + 1);
          outView.setInt16(outIdx, sample1, true);
          outIdx += 2;
        }
      }
    }
    return outBuf;
  }

  private static decodeSample(code: number, predictors: number[], indices: number[], ch: number): number {
    let idx = Math.min(Math.max(indices[ch], 0), 88);
    let step = AdpcmDecoder.STEP_TABLE[idx];
    let delta = step >> 3;
    if ((code & 4) !== 0) delta += step;
    if ((code & 2) !== 0) delta += step >> 1;
    if ((code & 1) !== 0) delta += step >> 2;
    if ((code & 8) !== 0) {
      predictors[ch] -= delta;
    } else {
      predictors[ch] += delta;
    }
    predictors[ch] = Math.min(Math.max(predictors[ch], -32768), 32767);
    indices[ch] = Math.min(Math.max(indices[ch] + AdpcmDecoder.INDEX_TABLE[code & 7], 0), 88);
    return predictors[ch];
  }
}
