/**
 * AudioWorker - 在独立线程中处理 UDP 音频数据接收
 * Worker 线程不受熄屏降频影响，确保音频数据持续接收
 */

import { worker, MessageEvents } from '@kit.ArkTS';
import { socket } from '@kit.NetworkKit';

const UDP_PORT: number = 9288;
// 简化版包头解析（与 AudioPacket.deserialize 保持一致，HEADER_SIZE=44）
const HEADER_SIZE: number = 44;

let udpSocket: socket.UDPSocket | null = null;
const workerPort: worker.WorkerPort = worker.workerPort;

// 接收主线程消息
workerPort.onmessage = (e: MessageEvents) => {
  const msg = e.data;
  if (msg.type === 'start') {
    startUdp();
  } else if (msg.type === 'stop') {
    stopUdp();
  }
};

function startUdp(): void {
  try {
    udpSocket = socket.constructUDPSocketInstance();
    udpSocket.bind({ address: '0.0.0.0', port: UDP_PORT }).then(() => {
      udpSocket!.on('message', (info: socket.SocketMessageInfo) => {
        // 直接将原始 ArrayBuffer 转移到主线程（零拷贝）
        const rawPacket: ArrayBuffer = info.message.slice(0);
        workerPort.postMessage({
          type: 'audioPacket',
          data: rawPacket
        }, [rawPacket]);
      });

      udpSocket!.on('error', () => {
        // UDP 出错，尝试重建
        stopUdp();
        setTimeout(() => startUdp(), 1000);
      });

      udpSocket!.on('close', () => {
        // 被关闭，尝试重建
        setTimeout(() => startUdp(), 1000);
      });

      workerPort.postMessage({ type: 'status', message: `UDP bound to port ${UDP_PORT}` });
    }).catch(() => {
      workerPort.postMessage({ type: 'error', message: 'UDP bind failed' });
    });
  } catch (err) {
    workerPort.postMessage({ type: 'error', message: `UDP create failed: ${err}` });
  }
}

function stopUdp(): void {
  try {
    udpSocket?.off('message');
    udpSocket?.off('error');
    udpSocket?.off('close');
    udpSocket?.close();
  } catch (err) {
    // ignore
  }
  udpSocket = null;
}
