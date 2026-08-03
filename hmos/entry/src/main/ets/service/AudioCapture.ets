/**
 * 🤖 AI 辅助生成 — DeepSeek V4
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/AudioCapture.ets

import { audio } from '@kit.AudioKit';
import { backgroundTaskManager } from '@kit.BackgroundTasksKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { BackgroundTaskGuard } from './BackgroundTaskGuard';
import { Logger } from './Logger';


/**
 * 音频捕获回调类
 */
export class AudioCaptureCallback {
  onAudioData: (data: ArrayBuffer) => void = () => {};
  onError: (error: string) => void = () => {};
}

/**
 * 音频捕获服务（麦克风 → PC）
 */
export class AudioCapture {
  private audioCapturer: audio.AudioCapturer | null = null;
  private isCapturing: boolean = false;
  private callback: AudioCaptureCallback | null = null;
  private context: Context | null = null;

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

  setContext(context: Context): void {
    this.context = context;
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
      this.startBackgroundTask();
      Logger.info('AudioCapture', 'Started');
    } catch (err) {
      // 失败路径：释放已创建的 capturer，避免泄漏
      try {
        await this.audioCapturer?.release();
      } catch (e) {
        Logger.error('AudioCapture', `Cleanup release error: ${e}`);
      }
      this.audioCapturer = null;
      let errorMsg: string;
      if (err instanceof Error) {
        errorMsg = err.message;
      } else {
        errorMsg = String(err);
      }
      Logger.error('AudioCapture', `Start failed: ${errorMsg}`);
      this.callback?.onError(`启动麦克风捕获失败: ${errorMsg}`);
    }
  }

  async stop(): Promise<void> {
    this.isCapturing = false;
    this.stopBackgroundTask();
    const capturer = this.audioCapturer;
    this.audioCapturer = null;
    if (capturer) {
      try {
        capturer.off('readData');
      } catch (err) {
        Logger.error('AudioCapture', `off error: ${err}`);
      }
      // stop/release 相互独立：一个失败不阻断另一个，保证资源最终释放
      try {
        await capturer.stop();
      } catch (err) {
        Logger.error('AudioCapture', `Stop error: ${err}`);
      }
      try {
        await capturer.release();
      } catch (err) {
        Logger.error('AudioCapture', `Release error: ${err}`);
      }
    }
    Logger.info('AudioCapture', 'Stopped');
  }

  private async startBackgroundTask(): Promise<void> {
    if (!this.context) {
      return;
    }
    // 引用计数共享任务（与连接保活/播放共用）
    await BackgroundTaskGuard.acquire(this.context);
  }

  private async stopBackgroundTask(): Promise<void> {
    await BackgroundTaskGuard.release();
  }
}