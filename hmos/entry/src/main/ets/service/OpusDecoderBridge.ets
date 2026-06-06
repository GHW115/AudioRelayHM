// Opus decoder native module type declaration
// 与 opus_decoder.cpp 导出的 NAPI 函数一一对应
declare module 'libopus_decoder.so' {
  export function createDecoder(sampleRate: number, channels: number): number;
  export function pushInput(handle: number, data: ArrayBuffer): void;
  export function readOutput(handle: number): ArrayBuffer | undefined;
  export function destroyDecoder(handle: number): void;
}
