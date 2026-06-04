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