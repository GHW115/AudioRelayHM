/**
 * 🤖 AI 辅助生成 — Claude (Anthropic)
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/NetworkService.ets
// TCP 控制通道 + 音频数据发送（手机→PC 方向）
// PC→手机 方向的 UDP 音频接收由 NAPI C++ 层处理

import { socket } from '@kit.NetworkKit';
import { util } from '@kit.ArkTS';
import { AudioPacket, MessageType, ControlCommand, StreamDirection, EncodingType } from '../model/AudioPacket';
import { BusinessError } from '@kit.BasicServicesKit';

/**
 * 网络连接状态
 */
export enum ConnectionState {
  DISCONNECTED = 0,
  CONNECTING = 1,
  CONNECTED = 2,
}

/**
 * 网络服务：TCP 控制通道 + 手机→PC 音频发送
 */
export class NetworkService {
  private tcpSocket: socket.TCPSocket | null = null;
  private udpSendSocket: socket.UDPSocket | null = null;
  private host: string = '';
  private controlPort: number = 9287;
  private readonly udpPort: number = 9288;

  private _state: ConnectionState = ConnectionState.DISCONNECTED;
  private sequenceNumber: number = 0;
  private heartbeatTimer: number = -1;
  private autoReconnectTimer: number = -1;
  private readonly HEADER_SIZE: number = 44;
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

    if (this.autoReconnectTimer !== -1) {
      clearTimeout(this.autoReconnectTimer);
      this.autoReconnectTimer = -1;
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
      await this.createUdpSendSocket();

      console.info(`[NetworkService] Connected to ${host}:${port} (TCP control + NAPI UDP audio)`);
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
    if (this.autoReconnectTimer !== -1) {
      clearTimeout(this.autoReconnectTimer);
      this.autoReconnectTimer = -1;
    }
    this.closeUdpSendSocket();
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

  /**
   * 发送音频数据到 PC（手机→PC 方向，通过 UDP）
   */
  async sendAudioData(direction: StreamDirection, pcmData: ArrayBuffer,
    sampleRate: number, channels: number, encodingType?: number): Promise<void> {
    if (!this.udpSendSocket || this._state !== ConnectionState.CONNECTED) {
      return;
    }

    const packet = new AudioPacket();
    packet.msgType = MessageType.AUDIO_DATA;
    packet.direction = direction;
    packet.sequence = this.sequenceNumber++;
    packet.timestamp = Date.now();
    packet.sampleRate = sampleRate;
    packet.channels = channels;
    packet.bitsPerSample = 16;
    packet.payload = pcmData;
    packet.encodingType = EncodingType.PCM;

    try {
      const data = packet.serialize();
      const sendOpts: socket.UDPSendOptions = {
        data: data,
        address: {
          address: this.host,
          port: this.udpPort,
        },
      };
      await this.udpSendSocket.send(sendOpts);
    } catch (err) {
      console.error(`[NetworkService] UDP audio send failed: ${err}`);
    }
  }

  async sendConfig(encoding: number, bitrate: number, bufferMs: number): Promise<void> {
    const payload = new ArrayBuffer(9);
    const view = new DataView(payload);
    view.setUint8(0, encoding);
    view.setInt32(1, bitrate, true);
    view.setInt32(5, bufferMs, true);
    await this.sendControl(ControlCommand.CONFIG, payload);
  }

  async sendTimeSync(): Promise<void> {
    const payload = new ArrayBuffer(8);
    const view = new DataView(payload);
    let now: number = Date.now();
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

      let merged = new Uint8Array(this.recvBuf.byteLength + newBytes.byteLength);
      merged.set(this.recvView, 0);
      merged.set(new Uint8Array(newBytes), this.recvBuf.byteLength);
      this.recvBuf = merged.buffer.slice(0);
      this.recvView = new Uint8Array(this.recvBuf);

      while (this.recvBuf.byteLength >= this.HEADER_SIZE) {
        let payloadLen = this.recvView[40] | (this.recvView[41] << 8) |
          (this.recvView[42] << 16) | (this.recvView[43] << 24);
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
    }, 1000);
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
    this.closeUdpSendSocket();
    this._state = ConnectionState.DISCONNECTED;
    this.onStateChange?.(this._state);
    console.info('[NetworkService] Connection closed');

    if (this.host) {
      this.autoReconnectTimer = setTimeout(() => {
        console.info('[NetworkService] 尝试自动重连...');
        this.connect(this.host, this.controlPort).catch((err: Error) => {
          console.error(`[NetworkService] 自动重连失败: ${err.message}`);
        });
      }, 2000);
    }
  }

  /**
   * 创建 UDP 发送 socket（手机→PC 方向，不绑定本地端口）
   */
  private async createUdpSendSocket(): Promise<void> {
    try {
      this.udpSendSocket = socket.constructUDPSocketInstance();
      console.info('[NetworkService] UDP send socket created');
    } catch (err) {
      console.error(`[NetworkService] UDP send socket failed: ${err}`);
      this.udpSendSocket = null;
    }
  }

  private closeUdpSendSocket(): void {
    try {
      this.udpSendSocket?.close();
    } catch (err) {
      console.error(`[NetworkService] UDP send close error: ${err}`);
    }
    this.udpSendSocket = null;
  }
}
