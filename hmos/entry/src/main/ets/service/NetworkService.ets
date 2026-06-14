/**
 * 🤖 AI 辅助生成 — Claude (Anthropic)
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/NetworkService.ets

import { socket } from '@kit.NetworkKit';
import { util } from '@kit.ArkTS';
import { worker } from '@kit.ArkTS';
import { AudioPacket, MessageType, ControlCommand, StreamDirection, EncodingType } from '../model/AudioPacket';
import { BusinessError } from '@kit.BasicServicesKit';

interface WorkerMessage {
  data: Record<string, Object>;
}

interface WorkerError {
  message: string;
}

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
  private udpSocket: socket.UDPSocket | null = null;
  private audioWorker: worker.ThreadWorker | null = null;
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

    // 取消可能存在的自动重连
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
      await this.setupUdpSocket();
      this.startAudioWorker();

      console.info(`[NetworkService] Connected to ${host}:${port} (TCP control + UDP ${this.udpPort} audio)`);
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
    this.closeUdpSocket();
    this.stopAudioWorker();
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
    if (!this.udpSocket || this._state !== ConnectionState.CONNECTED) {
      console.warn('[NetworkService] Cannot send audio: UDP not ready');
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
    // 手机端始终发送 PCM（无 Opus 编码器），编码选择仅控制 Windows 端捕获
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
      await this.udpSocket.send(sendOpts);
    } catch (err) {
      console.error(`[NetworkService] UDP audio send failed: ${err}`);
    }
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
    }, 1000); // 1秒心跳，防止系统限制网络时TCP超时
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
    this.closeUdpSocket();
    this.stopAudioWorker();
    this._state = ConnectionState.DISCONNECTED;
    this.onStateChange?.(this._state);
    console.info('[NetworkService] Connection closed');

    // 自动重连：2秒后尝试重新连接
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
   * 创建并绑定 UDP socket，用于双向音频传输
   */
  private async setupUdpSocket(): Promise<void> {
    try {
      this.udpSocket = socket.constructUDPSocketInstance();

      // 绑定本地 UDP 端口 9288 以接收 PC 端音频
      const bindAddr: socket.NetAddress = {
        address: '0.0.0.0',
        port: this.udpPort,
      };
      await this.udpSocket.bind(bindAddr);

      // 监听 UDP 消息（PC 端发送的音频数据）
      this.udpSocket.on('message', (info: socket.SocketMessageInfo) => {
        this.handleUdpData(info.message);
      });

      this.udpSocket.on('error', (err: BusinessError) => {
        console.error(`[NetworkService] UDP error: ${err.message}`);
        // UDP 错误不触发断连，尝试重建
        this.setupUdpSocket().catch(() => {});
      });

      this.udpSocket.on('close', () => {
        console.info('[NetworkService] UDP socket closed');
        // 如果仍在连接状态，尝试重建 UDP
        if (this._state === ConnectionState.CONNECTED) {
          this.setupUdpSocket().catch(() => {});
        }
      });

      console.info(`[NetworkService] UDP socket bound to port ${this.udpPort}`);
    } catch (err) {
      console.error(`[NetworkService] UDP setup failed: ${err}`);
      this.udpSocket = null;
    }
  }

  /**
   * 处理 UDP 接收到的音频数据（无需 TCP 粘包处理）
   */
  private handleUdpData(data: ArrayBuffer | string): void {
    try {
      let buf: ArrayBuffer;
      if (typeof data === 'string') {
        buf = new util.TextEncoder().encodeInto(data).buffer.slice(0);
      } else {
        buf = data.slice(0);
      }

      const packet = AudioPacket.deserialize(buf);
      if (packet.msgType === MessageType.AUDIO_DATA) {
        this.onAudioData?.(packet);
      }
    } catch (err) {
      console.error(`[NetworkService] UDP parse failed: ${err}`);
    }
  }

  /**
   * 关闭 UDP socket
   */
  private closeUdpSocket(): void {
    try {
      this.udpSocket?.off('message');
      this.udpSocket?.close();
    } catch (err) {
      console.error(`[NetworkService] UDP close error: ${err}`);
    }
    this.udpSocket = null;
  }

  /**
   * 启动 AudioWorker 线程处理 UDP 音频接收（不受熄屏降频影响）
   */
  private startAudioWorker(): void {
    try {
      this.audioWorker = new worker.ThreadWorker('entry/ets/workers/AudioWorker.ets');
      this.audioWorker.onmessage = (e: WorkerMessage) => {
        const msg: Record<string, Object> = e.data;
        const msgType: string = msg['type'] as string;
        if (msgType === 'audioPacket') {
          try {
            const packet = AudioPacket.deserialize(msg['data'] as ArrayBuffer);
            if (packet.msgType === MessageType.AUDIO_DATA) {
              this.onAudioData?.(packet);
            }
          } catch (err) {
            console.error(`[NetworkService] Worker packet parse failed: ${err}`);
          }
        } else if (msgType === 'status') {
          console.info(`[AudioWorker] ${msg['message'] as string}`);
        } else if (msgType === 'error') {
          console.error(`[AudioWorker] ${msg['message'] as string}`);
        }
      };
      this.audioWorker.onerror = (err: WorkerError) => {
        console.error(`[AudioWorker] Worker error: ${err.message}`);
      };
      this.audioWorker.postMessage({ type: 'start' });
      console.info('[NetworkService] AudioWorker started');
    } catch (err) {
      console.error(`[NetworkService] AudioWorker create failed: ${err}`);
      this.audioWorker = null;
    }
  }

  /**
   * 停止 AudioWorker
   */
  private stopAudioWorker(): void {
    if (this.audioWorker) {
      try {
        this.audioWorker.postMessage({ type: 'stop' });
        this.audioWorker.terminate();
      } catch (err) {
        console.error(`[NetworkService] AudioWorker stop error: ${err}`);
      }
      this.audioWorker = null;
    }
  }
}
