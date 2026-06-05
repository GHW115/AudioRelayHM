/**
 * 🤖 AI 辅助生成 — Claude (Anthropic)
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/AudioCapture.ets

import { audio } from '@kit.AudioKit';

/**
 * 音频捕获回调类
 */
export class AudioCaptureCallback {
  onAudioData: (data: ArrayBuffer) => void = () => {};
  onError: (error: string) => void = () => {};
}

/**
 * IMA ADPCM 编码器（PCM → ADPCM，4:1 压缩）
 */
export class AdpcmEncoder {
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

  static encode(pcm: ArrayBuffer, channels: number): ArrayBuffer {
    const samples = new Int16Array(pcm);
    const totalSamples = samples.length;
    const frames = totalSamples / channels;
    const encodedDataLen = Math.floor(totalSamples / 2);
    const output = new ArrayBuffer(channels * 8 + encodedDataLen);
    const outView = new DataView(output);

    const predictors: number[] = new Array(channels).fill(0);
    const indices: number[] = new Array(channels).fill(0);

    let outIdx = channels * 8;
    let inIdx = 0;

    for (let f = 0; f < frames; f++) {
      for (let ch = 0; ch < channels; ch += 2) {
        const code0 = AdpcmEncoder.encodeSample(samples[inIdx], predictors, indices, ch);
        inIdx++;
        let code1 = 0;
        if (ch + 1 < channels) {
          code1 = AdpcmEncoder.encodeSample(samples[inIdx], predictors, indices, ch + 1);
          inIdx++;
        }
        outView.setUint8(outIdx++, (code0 | (code1 << 4)) & 0xFF);
      }
    }

    for (let ch = 0; ch < channels; ch++) {
      outView.setInt32(ch * 4, predictors[ch], true);
      outView.setInt32(channels * 4 + ch * 4, indices[ch], true);
    }

    return output;
  }

  private static encodeSample(sample: number, predictors: number[], indices: number[], ch: number): number {
    const predictor = predictors[ch];
    let index = indices[ch];
    const step = AdpcmEncoder.STEP_TABLE[Math.min(Math.max(index, 0), 88)];

    let diff = sample - predictor;
    let code = 0;
    if (diff < 0) {
      code = 8;
      diff = -diff;
    }

    let delta = step >> 3;
    let tmp = step;
    if (diff >= tmp) { code |= 4; diff -= tmp; delta += tmp; }
    tmp >>= 1;
    if (diff >= tmp) { code |= 2; diff -= tmp; delta += tmp; }
    tmp >>= 1;
    if (diff >= tmp) { code |= 1; delta += tmp; }

    let newPredictor: number;
    if ((code & 8) !== 0) {
      newPredictor = predictor - delta;
    } else {
      newPredictor = predictor + delta;
    }
    predictors[ch] = Math.min(Math.max(newPredictor, -32768), 32767);
    indices[ch] = Math.min(Math.max(index + AdpcmEncoder.INDEX_TABLE[code & 7], 0), 88);

    return code & 0x0F;
  }
}

/**
 * 音频捕获服务（麦克风 → PC）
 */
export class AudioCapture {
  private audioCapturer: audio.AudioCapturer | null = null;
  private isCapturing: boolean = false;
  private callback: AudioCaptureCallback | null = null;

  private _sampleRate: number = 48000;
  private _channels: number = 2;
  private _bitsPerSample: number = 16;

  get sampleRate(): number {
    return this._sampleRate;
  }

  get channels(): number {
    return this._channels;
  }

  get bitsPerSample(): number {
    return this._bitsPerSample;
  }

  get capturing(): boolean {
    return this.isCapturing;
  }

  setCallback(callback: AudioCaptureCallback): void {
    this.callback = callback;
  }

  async start(): Promise<void> {
    if (this.isCapturing) {
      return;
    }

    try {
      const capturerOptions: audio.AudioCapturerOptions = {
        streamInfo: {
          samplingRate: audio.AudioSamplingRate.SAMPLE_RATE_48000,
          channels: audio.AudioChannel.CHANNEL_2,
          sampleFormat: audio.AudioSampleFormat.SAMPLE_FORMAT_S16LE,
          encodingType: audio.AudioEncodingType.ENCODING_TYPE_RAW,
        },
        capturerInfo: {
          source: audio.SourceType.SOURCE_TYPE_MIC,
          capturerFlags: 0,
        },
      };

      this.audioCapturer = await audio.createAudioCapturer(capturerOptions);

      this.audioCapturer.on('readData', (buffer: ArrayBuffer) => {
        if (this.isCapturing && this.callback && buffer.byteLength > 0) {
          this.callback.onAudioData(buffer);
        }
      });

      await this.audioCapturer.start();
      this.isCapturing = true;
      console.info('[AudioCapture] Started');
    } catch (err) {
      let errorMsg: string;
      if (err instanceof Error) {
        errorMsg = err.message;
      } else {
        errorMsg = String(err);
      }
      console.error(`[AudioCapture] Start failed: ${errorMsg}`);
      this.callback?.onError(`启动麦克风捕获失败: ${errorMsg}`);
    }
  }

  async stop(): Promise<void> {
    this.isCapturing = false;
    try {
      this.audioCapturer?.off('readData');
      await this.audioCapturer?.stop();
      await this.audioCapturer?.release();
    } catch (err) {
      console.error(`[AudioCapture] Stop error: ${err}`);
    }
    this.audioCapturer = null;
    console.info('[AudioCapture] Stopped');
  }
}