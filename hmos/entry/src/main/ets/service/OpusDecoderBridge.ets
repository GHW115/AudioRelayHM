// Opus decoder native module type declaration
declare module 'libopus_decoder.so' {
  export function createDecoder(sampleRate: number, channels: number): number;
  export function decodeFrame(handle: number, data: ArrayBuffer): ArrayBuffer | undefined;
  export function destroyDecoder(handle: number): void;
}
