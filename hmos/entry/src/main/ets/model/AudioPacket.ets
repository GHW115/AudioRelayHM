/**
 * 🤖 AI 辅助生成 — Claude (Anthropic)
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// model/AudioPacket.ets

/**
 * 消息类型
 */
export enum MessageType {
  /** 控制消息：握手/心跳/状态 */
  CONTROL = 0,
  /** 音频数据：PCM 裸流 */
  AUDIO_DATA = 1,
}

/**
 * 控制命令
 */
export enum ControlCommand {
  /** 握手请求 */
  HANDSHAKE = 0,
  /** 握手响应 */
  HANDSHAKE_ACK = 1,
  /** 心跳 */
  HEARTBEAT = 2,
  /** 开始音频流 */
  START_STREAM = 3,
  /** 停止音频流 */
  STOP_STREAM = 4,
  /** 音量设置 */
  VOLUME = 5,
  /** 配置参数（编码/码率/缓冲） */
  CONFIG = 6,
  /** 延迟报告（手机→PC） */
  LATENCY_REPORT = 7,
  /** 时钟同步请求 */
  TIME_SYNC = 8,
}

/**
 * 音频流方向
 */
export enum StreamDirection {
  /** PC → 手机（PC 音频 → 手机播放） */
  PC_TO_PHONE = 0,
  /** 手机 → PC（手机麦克风 → PC 播放） */
  PHONE_TO_PC = 1,
}

/**
 * 音频编码类型
 */
export enum EncodingType {
  /** PCM 裸流 */
  PCM = 0,
  /** Opus 压缩 */
  OPUS = 1,
  /** ADPCM 压缩 */
  ADPCM = 2,
}

/**
 * 网络数据包（统一格式）
 */
export class AudioPacket {
  /** 消息类型 */
  msgType: MessageType = MessageType.CONTROL;
  /** 控制命令（仅 CONTROL 消息使用） */
  command?: ControlCommand;
  /** 音频流方向（仅 AUDIO_DATA 消息使用） */
  direction?: StreamDirection;
  /** 音频编码类型 */
  encodingType: EncodingType = EncodingType.PCM;
  /** 序列号 */
  sequence: number = 0;
  /** 时间戳（毫秒） */
  timestamp: number = 0;
  /** 音频采样率 */
  sampleRate?: number;
  /** 声道数 */
  channels?: number;
  /** 位深 */
  bitsPerSample?: number;
  /** 负载数据 */
  payload: ArrayBuffer = new ArrayBuffer(0);

  /**
   * 序列化为 ArrayBuffer
   */
  serialize(): ArrayBuffer {
    const headerSize = 28;
    const buffer = new ArrayBuffer(headerSize + this.payload.byteLength);
    const view = new DataView(buffer);
    let offset = 0;

    view.setUint8(offset, this.msgType); offset += 1;
    view.setUint8(offset, this.command ?? 0xFF); offset += 1;
    view.setUint8(offset, this.direction ?? 0xFF); offset += 1;
    view.setUint8(offset, this.encodingType); offset += 1;

    view.setInt32(offset, this.sequence, true); offset += 4;
    // timestamp 以 Int64 LE 格式写入（拆分为低32位+高32位），与 Windows BinaryWriter.Write(long) 一致
    let tsLow: number = (this.timestamp >>> 0);
    let tsHigh: number = Math.floor(this.timestamp / 0x100000000);
    view.setUint32(offset, tsLow, true); offset += 4;
    view.setInt32(offset, tsHigh, true); offset += 4;

    view.setInt32(offset, this.sampleRate ?? 0, true); offset += 4;
    view.setUint8(offset, this.channels ?? 0); offset += 1;
    view.setUint8(offset, this.bitsPerSample ?? 0); offset += 1;
    offset += 2;

    view.setInt32(offset, this.payload.byteLength, true); offset += 4;

    if (this.payload.byteLength > 0) {
      new Uint8Array(buffer, headerSize, this.payload.byteLength)
        .set(new Uint8Array(this.payload));
    }

    return buffer;
  }

  /**
   * 从 ArrayBuffer 反序列化
   */
  static deserialize(buffer: ArrayBuffer): AudioPacket {
    const view = new DataView(buffer);
    const packet = new AudioPacket();
    let offset = 0;

    packet.msgType = view.getUint8(offset); offset += 1;
    const cmd = view.getUint8(offset); offset += 1;
    if (cmd !== 0xFF) packet.command = cmd;
    const dir = view.getUint8(offset); offset += 1;
    if (dir !== 0xFF) packet.direction = dir;
    packet.encodingType = view.getUint8(offset) as EncodingType; offset += 1;

    packet.sequence = view.getInt32(offset, true); offset += 4;
    // timestamp 以 Int64 LE 格式读取（低32位+高32位重组），与 Windows BinaryReader.ReadInt64() 一致
    let tsLow: number = view.getUint32(offset, true);
    let tsHigh: number = view.getInt32(offset + 4, true);
    packet.timestamp = tsHigh * 0x100000000 + tsLow;
    offset += 8;

    packet.sampleRate = view.getInt32(offset, true); offset += 4;
    packet.channels = view.getUint8(offset); offset += 1;
    packet.bitsPerSample = view.getUint8(offset); offset += 1;
    offset += 2;

    const payloadLength = view.getInt32(offset, true); offset += 4;

    if (payloadLength > 0) {
      packet.payload = buffer.slice(offset, offset + payloadLength);
    }

    return packet;
  }
}

/**
 * 音频配置参数
 */
export class AudioConfig {
  sampleRate: number = 48000;
  channels: number = 2;
  bitsPerSample: number = 16;
  frameSize: number = 4096;

  /**
   * 计算每帧字节数
   */
  getFrameBytes(): number {
    return this.frameSize * this.channels * (this.bitsPerSample / 8);
  }
}
