/**
 * 🤖 AI 辅助生成 — Claude (Anthropic)
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/AudioPlay.ets
// 音频播放服务：通过 NAPI 原生管线（UDP接收 + Opus解码 + OHAudio渲染）播放PC音频
// AVSession + 后台任务保留在 ArkTS 层

import { audio } from '@kit.AudioKit';
import { backgroundTaskManager } from '@kit.BackgroundTasksKit';
import { wantAgent, WantAgent } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { avSession } from '@kit.AVSessionKit';
import { EncodingType } from '../model/AudioPacket';
import { nativeAudioInit, nativeAudioStart, nativeAudioStop, nativeAudioSetEncoding } from 'libopus_decoder.so';

/**
 * 音频播放回调类
 */
export class AudioPlayCallback {
  onError: (error: string) => void = () => {};
  onInfo: (info: string) => void = () => {};
}

/**
 * IMA ADPCM 解码器（保留用于未来可能的 ArkTS 层解码需求）
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
 * 音频播放服务（NAPI 原生管线控制 + AVSession + 后台任务）
 */
export class AudioPlay {
  private isPlaying: boolean = false;
  private callback: AudioPlayCallback | null = null;
  private context: Context | null = null;
  private avSessionObj: avSession.AVSession | null = null;

  private _sampleRate: number = 48000;
  private _channels: number = 2;
  private _bitsPerSample: number = 16;
  private bufferTargetMs: number = 200;

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
      // AVSession 必须在 NAPI 音频管线之前创建
      await this.createAVSession();
      this.startBackgroundTask();

      // 初始化 NAPI 原生音频管线（UDP接收 + Opus解码 + OHAudio渲染）
      const initResult: number = nativeAudioInit(this._sampleRate, this._channels);
      if (initResult !== 0) {
        throw new Error(`nativeAudioInit failed: ${initResult}`);
      }

      // 设置编码类型（0=PCM）
      nativeAudioSetEncoding(0);

      // 启动管线
      const startResult: number = nativeAudioStart();
      if (startResult !== 0) {
        throw new Error(`nativeAudioStart failed: ${startResult}`);
      }

      this.isPlaying = true;
      this.callback?.onInfo('NAPI 原生音频管线已启动');
      console.info('[AudioPlay] Native audio pipeline started');
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

  /**
   * 接收音频数据（兼容旧接口，NAPI 管线模式下数据直接由 C++ 层处理，此方法为空）
   */
  writeData(data: ArrayBuffer, encodingType: EncodingType = EncodingType.PCM): void {
    // NAPI 管线模式下，UDP 数据直接由 C++ 层接收和处理
    // 此方法保留仅为兼容旧调用方，实际不做任何操作
  }

  async stop(): Promise<void> {
    this.isPlaying = false;
    this.stopBackgroundTask();
    this.destroyAVSession();

    try {
      nativeAudioStop();
    } catch (err) {
      console.error(`[AudioPlay] Native stop error: ${err}`);
    }

    console.info('[AudioPlay] Stopped');
  }

  // AVSession 完整流程：metadata → events → activate → playbackState
  private async createAVSession(): Promise<void> {
    if (!this.context) {
      return;
    }
    try {
      this.avSessionObj = await avSession.createAVSession(
        this.context, 'AudioRelay', 'audio'
      );

      // 1. 设置媒体元数据
      const metadata: avSession.AVMetadata = {
        assetId: 'audio-relay-stream',
        title: 'PC 音频串流',
        artist: 'AudioRelay',
      };
      await this.avSessionObj.setAVMetadata(metadata);

      // 2. 注册播控事件（必须在 activate 之前）
      this.avSessionObj.on('play', () => {
        console.info('[AudioPlay] AVSession play 事件');
      });
      this.avSessionObj.on('pause', () => {
        console.info('[AudioPlay] AVSession pause 事件（忽略）');
      });

      // 3. 激活会话
      await this.avSessionObj.activate();

      // 4. 设置播放状态
      const playbackState: avSession.AVPlaybackState = {
        state: avSession.PlaybackState.PLAYBACK_STATE_PLAY,
        speed: 1.0,
      };
      await this.avSessionObj.setAVPlaybackState(playbackState);

      this.callback?.onInfo('AVSession 已激活');
      console.info('[AudioPlay] AVSession 完整初始化完成');
    } catch (err) {
      let msg = `创建 AVSession 失败: ${(err as BusinessError).code}`;
      console.error('[AudioPlay] ' + msg);
      this.callback?.onInfo(msg);
    }
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
}
