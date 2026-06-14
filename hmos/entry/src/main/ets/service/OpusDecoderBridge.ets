// Opus decoder + native audio pipeline NAPI module type declaration
// 与 opus_decoder.cpp 导出的 NAPI 函数一一对应
declare module 'libopus_decoder.so' {
  // 旧 API（兼容）
  export function createDecoder(sampleRate: number, channels: number): number;
  export function pushInput(handle: number, data: ArrayBuffer): void;
  export function readOutput(handle: number): ArrayBuffer | undefined;
  export function destroyDecoder(handle: number): void;

  // 新 API（原生音频管线：UDP接收 + Opus解码 + OHAudio渲染）
  export function nativeAudioInit(sampleRate: number, channels: number): number;
  export function nativeAudioStart(): number;
  export function nativeAudioStop(): number;
  export function nativeAudioSetEncoding(encodingType: number): void;
  export function nativeAudioGetQueueSize(): number;
}
