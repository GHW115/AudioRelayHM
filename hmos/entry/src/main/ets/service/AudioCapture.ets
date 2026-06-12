/**
 * 🤖 AI 辅助生成 — Claude (Anthropic)
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/AudioCapture.ets

import { audio } from '@kit.AudioKit';
import { backgroundTaskManager } from '@kit.BackgroundTasksKit';
import { wantAgent, WantAgent } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';

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
    this.stopBackgroundTask();
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

  private async startBackgroundTask(): Promise<void> {
    if (!this.context) {
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
        backgroundTaskManager.BackgroundMode.AUDIO_RECORDING,
        wantAgentObj
      );
      console.info('[AudioCapture] 后台录音任务已启动');
    } catch (err) {
      let error = err as BusinessError;
      console.error(`[AudioCapture] 后台任务失败: code=${error.code}, message=${error.message}`);
    }
  }

  private async stopBackgroundTask(): Promise<void> {
    if (this.context) {
      try {
        await backgroundTaskManager.stopBackgroundRunning(this.context);
      } catch (err) {
        console.error(`[AudioCapture] 停止后台任务失败: ${err}`);
      }
    }
  }
}