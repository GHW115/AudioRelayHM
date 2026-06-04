/**
 * 🤖 AI 辅助生成 — Claude (Anthropic)
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// Opus decoder native module type declaration
declare module 'libopus_decoder.so' {
  export function createDecoder(sampleRate: number, channels: number): number;
  export function decodeFrame(handle: number, data: ArrayBuffer): ArrayBuffer | undefined;
  export function destroyDecoder(handle: number): void;
}
