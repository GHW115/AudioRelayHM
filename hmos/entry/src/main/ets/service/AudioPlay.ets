/**
 * 🤖 AI 辅助生成 — DeepSeek V4
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/AudioPlay.ets
// 音频播放服务：通过 NAPI 原生管线（UDP接收 + Opus解码 + OHAudio渲染）播放PC音频
// AVSession + 后台任务保留在 ArkTS 层

import { audio } from '@kit.AudioKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { avSession } from '@kit.AVSessionKit';
import { EncodingType } from '../model/AudioPacket';
import { BackgroundTaskGuard } from './BackgroundTaskGuard';
import { nativeAudioInit, nativeAudioStart, nativeAudioStop, nativeAudioSetEncoding, nativeAudioSetBufferMs } from 'libopus_decoder.so';
import { Logger } from './Logger';


/**
 * 音频播放回调类
 */
export class AudioPlayCallback {
  onError: (error: string) => void = () => {};
  onInfo: (info: string) => void = () => {};
}

/**

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
    try {
      nativeAudioSetBufferMs(ms);
    } catch (err) {
      Logger.error('AudioPlay', `nativeAudioSetBufferMs failed: ${err}`);
    }
    Logger.info('AudioPlay', `Buffer target set to ${ms}ms`);
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

      // 初始化 NAPI 原生音频管线（UDP接收 + Opus解码 + OHAudio渲染）
      const initResult: number = nativeAudioInit(this._sampleRate, this._channels);
      if (initResult !== 0) {
        throw new Error(`nativeAudioInit failed: ${initResult}`);
      }

      // 设置编码类型（0=PCM）
      nativeAudioSetEncoding(0);
      // 应用用户设置的缓冲上限
      nativeAudioSetBufferMs(this.bufferTargetMs);

      // 启动管线
      const startResult: number = nativeAudioStart();
      if (startResult !== 0) {
        throw new Error(`nativeAudioStart failed: ${startResult}`);
      }

      // 音频已真实开始渲染后才启动后台任务：
      // AUDIO_PLAYBACK 连续任务校验要求应用处于实际播放状态，先启动会报 9800005
      // (Continuous task verification failed)
      await this.startBackgroundTask();

      this.isPlaying = true;
      this.callback?.onInfo('NAPI 原生音频管线已启动');
      Logger.info('AudioPlay', 'Native audio pipeline started');
    } catch (err) {
      // 失败路径清理：释放已创建的 AVSession / 后台任务 / native 管线，避免泄漏累积
      this.isPlaying = false;
      this.stopBackgroundTask();
      this.destroyAVSession();
      try {
        nativeAudioStop(); // g_ctx 为 null 时内部返回 -1，安全
      } catch (e) {
        Logger.error('AudioPlay', `Cleanup native error: ${e}`);
      }
      let errorMsg: string;
      if (err instanceof Error) {
        errorMsg = err.message;
      } else {
        errorMsg = String(err);
      }
      Logger.error('AudioPlay', `Start failed: ${errorMsg}`);
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
      Logger.error('AudioPlay', `Native stop error: ${err}`);
    }

    Logger.info('AudioPlay', 'Stopped');
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
        Logger.info('AudioPlay', 'AVSession play 事件');
      });
      this.avSessionObj.on('pause', () => {
        Logger.info('AudioPlay', 'AVSession pause 事件（忽略）');
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
      Logger.info('AudioPlay', 'AVSession 完整初始化完成');
    } catch (err) {
      let msg = `创建 AVSession 失败: ${(err as BusinessError).code}`;
      Logger.error('AudioPlay', msg);
      this.callback?.onInfo(msg);
    }
  }

  private async startBackgroundTask(): Promise<void> {
    if (!this.context) {
      this.callback?.onInfo('未设置 context，无法启动后台任务');
      return;
    }
    // 引用计数共享任务（与连接保活/录音共用），避免 stopBackgroundRunning 连带停掉其他保活
    const ok: boolean = await BackgroundTaskGuard.acquire(this.context);
    this.callback?.onInfo(ok ? '后台长时任务已启动' : '后台任务启动失败');
  }

  private async stopBackgroundTask(): Promise<void> {
    await BackgroundTaskGuard.release();
  }

  private async destroyAVSession(): Promise<void> {
    if (this.avSessionObj) {
      try {
        await this.avSessionObj.destroy();
      } catch (err) {
        Logger.error('AudioPlay', `销毁 AVSession 失败: ${err}`);
      }
      this.avSessionObj = null;
    }
  }
}
