// Opus decoder + native audio pipeline NAPI module type declaration
// 与 opus_decoder.cpp 导出的 NAPI 函数一一对应
declare module 'libopus_decoder.so' {
  // 原生音频管线 API（UDP接收 + Opus解码 + OHAudio渲染）
  export function nativeAudioInit(sampleRate: number, channels: number): number;
  export function nativeAudioStart(): number;
  export function nativeAudioStop(): number;
  export function nativeAudioSetEncoding(encodingType: number): void;
  export function nativeAudioGetQueueSize(): number;
  export function nativeAudioSetClockOffset(offsetMs: number): void;
  export function nativeAudioGetLatencyStats(): ArrayBuffer;  // 16字节: 4个int32 LE
  export function nativeAudioSetBufferMs(ms: number): void;   // 设置环形缓冲上限(ms)
  export function nativeAudioSetVolume(volume: number): void; // 设置播放音量(0.0~1.0)
}
