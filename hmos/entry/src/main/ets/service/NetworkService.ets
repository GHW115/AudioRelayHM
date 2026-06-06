/**
 * 🤖 AI 辅助生成 — Claude (Anthropic)
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/NetworkService.ets

import { socket } from '@kit.NetworkKit';
import { util } from '@kit.ArkTS';
import { AudioPacket, MessageType, ControlCommand, StreamDirection, EncodingType } from '../model/AudioPacket';
import { BusinessError } from '@kit.BasicServicesKit';

/**
 * 网络连接状�? */
export enum ConnectionState {
  DISCONNECTED = 0,
  CONNECTING = 1,
  CONNECTED = 2,
}

/**
 * 网络服务：负责与 Windows 端的 TCP/UDP 通信
 */
export class NetworkService {
  private tcpSocket: socket.TCPSocket | null = null;
  private host: string = '';
  private controlPort: number = 9287;

  private _state: ConnectionState = ConnectionState.DISCONNECTED;
  private sequenceNumber: number = 0;
  private heartbeatTimer: number = -1;
  private readonly HEADER_SIZE: number = 28;
  private recvBuf: ArrayBuffer = new ArrayBuffer(0);
  private recvView: Uint8Array = new Uint8Array(0);

  onStateChange?: (state: ConnectionState) => void;
  onAudioData?: (packet: AudioPacket) => void;
  onControlMessage?: (packet: AudioPacket) => void;
  onError?: (error: string) => void;

  get state(): ConnectionState {
    return this._state;
  }

  async connect(host: string, port: number = 9287): Promise<void> {
    if (this._state === ConnectionState.CONNECTING ||
      this._state === ConnectionState.CONNECTED) {
      return;
    }

    this.host = host;
    this.controlPort = port;
    this._state = ConnectionState.CONNECTING;
    this.onStateChange?.(this._state);

    try {
      this.tcpSocket = socket.constructTCPSocketInstance();

      this.tcpSocket.on('message', (info: socket.SocketMessageInfo) => {
        this.handleData(info.message);
      });

      this.tcpSocket.on('close', () => {
        this.handleDisconnect();
      });

      this.tcpSocket.on('error', (err: BusinessError) => {
        console.error(`[NetworkService] Socket error: ${err.message}`);
        this.onError?.(err.message);
      });

      const addr: socket.NetAddress = {
        address: this.host,
        port: this.controlPort,
      };
      const connectOpts: socket.TCPConnectOptions = {
        address: addr,
        timeout: 5000,
      };
      await this.tcpSocket.connect(connectOpts);

      this._state = ConnectionState.CONNECTED;
      this.onStateChange?.(this._state);

      await this.sendHandshake();
      this.startHeartbeat();

      console.info(`[NetworkService] Connected to ${host}:${port}`);
    } catch (err) {
      this._state = ConnectionState.DISCONNECTED;
      this.onStateChange?.(this._state);
      let errorMsg: string;
      if (err instanceof Error) {
        errorMsg = err.message;
      } else {
        errorMsg = String(err);
      }
      console.error(`[NetworkService] Connect failed: ${errorMsg}`);
      this.onError?.(`连接失败: ${errorMsg}`);
      throw new Error(errorMsg);
    }
  }

  async disconnect(): Promise<void> {
    this.stopHeartbeat();
    this._state = ConnectionState.DISCONNECTED;
    this.onStateChange?.(this._state);

    try {
      this.tcpSocket?.off('message');
      this.tcpSocket?.off('close');
      this.tcpSocket?.off('error');
      await this.tcpSocket?.close();
    } catch (err) {
      console.error(`[NetworkService] Disconnect error: ${err}`);
    }
    this.tcpSocket = null;
    console.info('[NetworkService] Disconnected');
  }

  async sendAudioData(direction: StreamDirection, pcmData: ArrayBuffer,
    sampleRate: number, channels: number, encodingType?: number): Promise<void> {
    const packet = new AudioPacket();
    packet.msgType = MessageType.AUDIO_DATA;
    packet.direction = direction;
    packet.sequence = this.sequenceNumber++;
    packet.timestamp = Date.now();
    packet.sampleRate = sampleRate;
    packet.channels = channels;
    packet.bitsPerSample = 16;
    packet.payload = pcmData;
    // 手机端始终发送 PCM（无 Opus 编码器），编码选择仅控制 Windows 端捕获
    packet.encodingType = EncodingType.PCM;

    await this.sendPacket(packet);
  }

  async sendConfig(encoding: number, bitrate: number, bufferMs: number): Promise<void> {
    const payload = new ArrayBuffer(9);
    const view = new DataView(payload);
    view.setUint8(0, encoding);           // 编码类型: 0=PCM, 1=Opus, 2=ADPCM
    view.setInt32(1, bitrate, true);       // 码率 kbps
    view.setInt32(5, bufferMs, true);      // 缓冲 ms
    await this.sendControl(ControlCommand.CONFIG, payload);
  }

  async sendTimeSync(): Promise<void> {
    const payload = new ArrayBuffer(8);
    const view = new DataView(payload);
    let now: number = Date.now();
    // 以 Int64 LE 格式写入时间戳
    view.setUint32(0, now >>> 0, true);
    view.setInt32(4, Math.floor(now / 0x100000000), true);
    await this.sendControl(ControlCommand.TIME_SYNC, payload);
  }

  async sendControl(command: ControlCommand, payload?: ArrayBuffer): Promise<void> {
    const packet = new AudioPacket();
    packet.msgType = MessageType.CONTROL;
    packet.command = command;
    packet.sequence = this.sequenceNumber++;
    packet.timestamp = Date.now();
    if (payload) {
      packet.payload = payload;
    }

    await this.sendPacket(packet);
  }

  private async sendPacket(packet: AudioPacket): Promise<void> {
    if (this._state !== ConnectionState.CONNECTED || !this.tcpSocket) {
      console.warn('[NetworkService] Cannot send: not connected');
      return;
    }

    try {
      const data = packet.serialize();
      const sendOpts: socket.TCPSendOptions = { data: data };
      await this.tcpSocket.send(sendOpts);
    } catch (err) {
      console.error(`[NetworkService] Send failed: ${err}`);
      this.handleDisconnect();
    }
  }

  private handleData(data: ArrayBuffer | string): void {
    try {
      let newBytes: ArrayBuffer;
      if (typeof data === 'string') {
        newBytes = new util.TextEncoder().encodeInto(data).buffer.slice(0);
      } else {
        newBytes = data.slice(0);
      }

      // 合并到接收缓冲区
      let merged = new Uint8Array(this.recvBuf.byteLength + newBytes.byteLength);
      merged.set(this.recvView, 0);
      merged.set(new Uint8Array(newBytes), this.recvBuf.byteLength);
      this.recvBuf = merged.buffer.slice(0);
      this.recvView = new Uint8Array(this.recvBuf);

      // 循环解析完整包
      while (this.recvBuf.byteLength >= this.HEADER_SIZE) {
        let payloadLen = this.recvView[24] | (this.recvView[25] << 8) |
          (this.recvView[26] << 16) | (this.recvView[27] << 24);
        let totalLen = this.HEADER_SIZE + payloadLen;

        if (this.recvBuf.byteLength < totalLen) {
          break;
        }

        let packetBuf: ArrayBuffer = this.recvBuf.slice(0, totalLen);
        let packet = AudioPacket.deserialize(packetBuf);

        this.recvBuf = this.recvBuf.slice(totalLen);
        this.recvView = new Uint8Array(this.recvBuf);

        if (packet.msgType === MessageType.CONTROL) {
          this.onControlMessage?.(packet);
        } else if (packet.msgType === MessageType.AUDIO_DATA) {
          this.onAudioData?.(packet);
        }
      }
    } catch (err) {
      console.error(`[NetworkService] Parse data failed: ${err}`);
    }
  }

  private async sendHandshake(): Promise<void> {
    const encoder = new util.TextEncoder();
    let u8: Uint8Array = encoder.encodeInto('AudioRelayHMOS');
    const payload: ArrayBuffer = u8.buffer as ArrayBuffer;
    await this.sendControl(ControlCommand.HANDSHAKE, payload);
  }

  private startHeartbeat(): void {
    this.stopHeartbeat();
    this.heartbeatTimer = setInterval(() => {
      void this.sendControl(ControlCommand.HEARTBEAT);
    }, 5000);
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer !== -1) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = -1;
    }
  }

  private handleDisconnect(): void {
    if (this._state === ConnectionState.DISCONNECTED) {
      return;
    }
    this.stopHeartbeat();
    this._state = ConnectionState.DISCONNECTED;
    this.onStateChange?.(this._state);
    console.info('[NetworkService] Connection closed');
  }
}
