/**
 * 🤖 AI 辅助生成 — Claude (Anthropic)
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/AudioPlay.ets

import { audio } from '@kit.AudioKit';
import { backgroundTaskManager } from '@kit.BackgroundTasksKit';
import { wantAgent, WantAgent } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { avSession } from '@kit.AVSessionKit';
import { EncodingType } from '../model/AudioPacket';
import { createDecoder, pushInput, readOutput, destroyDecoder } from 'libopus_decoder.so';

/**
 * 音频播放回调类
 */
export class AudioPlayCallback {
  onError: (error: string) => void = () => {};
  onInfo: (info: string) => void = () => {};
}

/**
 * IMA ADPCM 解码器
 */
class AdpcmDecoder {
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

  /**
   * 解码 ADPCM 数据为 PCM 16-bit short[]，再转 ArrayBuffer
   */
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

/**
 * 音频播放服务（接收 PC 音频并播放）
 */
export class AudioPlay {
  private audioRenderer: audio.AudioRenderer | null = null;
  private isPlaying: boolean = false;
  private callback: AudioPlayCallback | null = null;
  private context: Context | null = null;
  private avSessionObj: avSession.AVSession | null = null;

  private _sampleRate: number = 48000;
  private _channels: number = 2;
  private _bitsPerSample: number = 16;

  // 缓冲区
  private bufferQueue: ArrayBuffer[] = [];
  private bufferSize: number = 0;
  private bufferTargetMs: number = 200;
  private flushTaskId: number = -1;
  private opusHandle: number = 0;
  private opusInitDone: boolean = false;
  private lastEncodingType: EncodingType = EncodingType.PCM;

  get sampleRate(): number {
    return this._sampleRate;
  }

  get channels(): number {
    return this._channels;
  }

  get bitsPerSample(): number {
    return this._bitsPerSample;
  }

  get playing(): boolean {
    return this.isPlaying;
  }

  setBufferDuration(ms: number): void {
    this.bufferTargetMs = ms;
    console.info(`[AudioPlay] Buffer target set to ${ms}ms`);
  }

  setCallback(callback: AudioPlayCallback): void {
    this.callback = callback;
  }

  setContext(context: Context): void {
    this.context = context;
  }

  async start(): Promise<void> {
    if (this.isPlaying) {
      return;
    }

    try {
      const rendererOptions: audio.AudioRendererOptions = {
        streamInfo: {
          samplingRate: audio.AudioSamplingRate.SAMPLE_RATE_48000,
          channels: audio.AudioChannel.CHANNEL_2,
          sampleFormat: audio.AudioSampleFormat.SAMPLE_FORMAT_S16LE,
          encodingType: audio.AudioEncodingType.ENCODING_TYPE_RAW,
        },
        rendererInfo: {
          usage: audio.StreamUsage.STREAM_USAGE_MUSIC,
          rendererFlags: 0,
        },
      };

      this.audioRenderer = await audio.createAudioRenderer(rendererOptions);

      // 注册系统数据回调（拉取模式）：系统主动要求数据，完美匹配实时速率
      this.audioRenderer.on('writeData', (buffer: ArrayBuffer): audio.AudioDataCallbackResult => {
        return this.onWriteData(buffer);
      });

      await this.audioRenderer.start();
      this.isPlaying = true;

      this.createAVSession();
      this.startBackgroundTask();
      this.initOpusDecoder();
    } catch (err) {
      let errorMsg: string;
      if (err instanceof Error) {
        errorMsg = err.message;
      } else {
        errorMsg = String(err);
      }
      console.error(`[AudioPlay] Start failed: ${errorMsg}`);
      this.callback?.onError(`启动音频播放失败: ${errorMsg}`);
    }
  }

  private async createAVSession(): Promise<void> {
    if (!this.context) {
      return;
    }
    try {
      this.avSessionObj = await avSession.createAVSession(
        this.context, 'AudioRelay', 'audio'
      );
      await this.avSessionObj.activate();
      this.callback?.onInfo('AVSession 已激活');
      console.info('[AudioPlay] AVSession created');
    } catch (err) {
      let msg = `创建 AVSession 失败: ${(err as BusinessError).code}`;
      console.error('[AudioPlay] ' + msg);
      this.callback?.onInfo(msg);
    }
  }

  // 系统 writeData 回调：填充 buffer 并返回 VALID，系统自动播放
  private onWriteData(buffer: ArrayBuffer): audio.AudioDataCallbackResult {
    if (!this.isPlaying) {
      return audio.AudioDataCallbackResult.INVALID;
    }
    // 从 Opus 解码器取 PCM
    if (this.opusHandle !== 0) {
      while (true) {
        let pcm: ArrayBuffer | undefined = readOutput(this.opusHandle);
        if (pcm) {
          this.bufferQueue.push(pcm);
          this.bufferSize += pcm.byteLength;
        } else {
          break;
        }
      }
    }
    // 填充系统提供的 buffer（必须填满，否则杂音）
    let view: Uint8Array = new Uint8Array(buffer);
    let filled: number = 0;
    while (filled < buffer.byteLength && this.bufferQueue.length > 0) {
      let f: ArrayBuffer = this.bufferQueue.shift()!;
      this.bufferSize -= f.byteLength;
      let remaining: number = buffer.byteLength - filled;
      if (f.byteLength <= remaining) {
        view.set(new Uint8Array(f), filled);
        filled += f.byteLength;
      } else {
        view.set(new Uint8Array(f, 0, remaining), filled);
        let tail: ArrayBuffer = f.slice(remaining);
        this.bufferQueue.unshift(tail);
        this.bufferSize += tail.byteLength;
        filled += remaining;
      }
    }
    return audio.AudioDataCallbackResult.VALID;
  }

  async writeData(data: ArrayBuffer, encodingType: EncodingType = EncodingType.PCM): Promise<void> {
    if (!this.isPlaying) {
      return;
    }
    // 检测编码切换：清理旧缓冲，重置解码器
    if (encodingType !== this.lastEncodingType) {
      console.info(`[AudioPlay] 编码切换: ${this.lastEncodingType} -> ${encodingType}，清理缓冲`);
      this.bufferQueue = [];
      this.bufferSize = 0;
      if (encodingType === EncodingType.OPUS || this.lastEncodingType === EncodingType.OPUS) {
        this.resetOpusDecoder();
      }
      this.lastEncodingType = encodingType;
    }
    if (encodingType === EncodingType.ADPCM) {
      let pcm: ArrayBuffer = AdpcmDecoder.decode(data, this._channels);
      this.bufferQueue.push(pcm);
      this.bufferSize += pcm.byteLength;
    } else if (encodingType === EncodingType.OPUS && this.opusHandle !== 0) {
      pushInput(this.opusHandle, data);
    } else {
      this.bufferQueue.push(data);
      this.bufferSize += data.byteLength;
    }
  }

  async stop(): Promise<void> {
    this.isPlaying = false;
    if (this.flushTaskId !== -1) {
      clearTimeout(this.flushTaskId);
      this.flushTaskId = -1;
    }
    this.bufferQueue = [];
    this.bufferSize = 0;
    this.stopBackgroundTask();
    this.destroyAVSession();
    this.destroyOpusDecoder();
    try {
      await this.audioRenderer?.stop();
      await this.audioRenderer?.release();
    } catch (err) {
      console.error(`[AudioPlay] Stop error: ${err}`);
    }
    this.audioRenderer = null;
    console.info('[AudioPlay] Stopped');
  }

  private async startBackgroundTask(): Promise<void> {
    if (!this.context) {
      this.callback?.onInfo('未设置 context，无法启动后台任务');
      return;
    }
    try {
      let wantAgentInfo: wantAgent.WantAgentInfo = {
        wants: [
          {
            bundleName: this.context.applicationInfo.name,
            abilityName: 'EntryAbility',
          }
        ],
        actionType: wantAgent.OperationType.START_ABILITY,
        requestCode: 0,
        wantAgentFlags: [wantAgent.WantAgentFlags.UPDATE_PRESENT_FLAG],
      };
      let wantAgentObj: WantAgent = await wantAgent.getWantAgent(wantAgentInfo);
      await backgroundTaskManager.startBackgroundRunning(
        this.context,
        backgroundTaskManager.BackgroundMode.AUDIO_PLAYBACK,
        wantAgentObj
      );
      this.callback?.onInfo('后台长时任务已启动');
      console.info('[AudioPlay] 后台任务已启动');
    } catch (err) {
      let error = err as BusinessError;
      let msg = `后台任务失败: code=${error.code}, message=${error.message}`;
      console.error('[AudioPlay] ' + msg);
      this.callback?.onInfo(msg);
    }
  }

  private async stopBackgroundTask(): Promise<void> {
    if (this.context) {
      try {
        await backgroundTaskManager.stopBackgroundRunning(this.context);
      } catch (err) {
        console.error(`[AudioPlay] 停止后台任务失败: ${err}`);
      }
    }
  }

  private async destroyAVSession(): Promise<void> {
    if (this.avSessionObj) {
      try {
        await this.avSessionObj.destroy();
      } catch (err) {
        console.error(`[AudioPlay] 销毁 AVSession 失败: ${err}`);
      }
      this.avSessionObj = null;
    }
  }

  private resetOpusDecoder(): void {
    if (this.opusHandle !== 0) {
      try {
        destroyDecoder(this.opusHandle);
      } catch (err) {
        console.error('[AudioPlay] Failed to destroy Opus decoder: ' + err);
      }
      this.opusHandle = 0;
    }
    this.initOpusDecoder();
    console.info('[AudioPlay] Opus decoder reset');
  }

  private initOpusDecoder(): void {
    if (this.opusHandle === 0) {
      try {
        this.opusHandle = createDecoder(this._sampleRate, this._channels);
        console.info('[AudioPlay] Opus decoder created, handle=' + this.opusHandle);
      } catch (err) {
        console.error('[AudioPlay] Failed to create Opus decoder: ' + err);
      }
    }
  }

  private destroyOpusDecoder(): void {
    if (this.opusHandle !== 0) {
      try {
        destroyDecoder(this.opusHandle);
      } catch (err) {
        console.error('[AudioPlay] Failed to destroy Opus decoder: ' + err);
      }
      this.opusHandle = 0;
    }
  }
}