/**
 * AudioWorker - 在独立线程中处理 UDP 音频数据接收
 * Worker 线程不受熄屏降频影响，确保音频数据持续接收
 */

import { worker } from '@kit.ArkTS';
import { socket } from '@kit.NetworkKit';

const UDP_PORT: number = 9288;

let udpSocket: socket.UDPSocket | null = null;
const workerPort: worker.ThreadWorker = worker.workerPort;

// 接收主线程消息
workerPort.onmessage = (e: MessageEvent) => {
  const msg: Record<string, Object> = e.data as Record<string, Object>;
  const msgType: string = msg['type'] as string;
  if (msgType === 'start') {
    startUdp();
  } else if (msgType === 'stop') {
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
        stopUdp();
        setTimeout(() => startUdp(), 1000);
      });

      udpSocket!.on('close', () => {
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
