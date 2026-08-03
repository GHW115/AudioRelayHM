/**
 * 🤖 AI 辅助生成 — DeepSeek V4
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/Logger.ets
// 文件日志：所有关键事件同时输出到 hilog（console）与沙箱文件，
// 便于问题不复现时用 hdc 拉取日志分析。
// 文件位置: <filesDir>/audiorelay.log（2MB 轮转到 .1）
// 拉取: hdc file recv /data/app/el2/100/base/com.example.audiorelayhmos/haps/entry/files/audiorelay.log .

import { fileIo as fs } from '@kit.CoreFileKit';

export class Logger {
  private static file: fs.File | null = null;
  private static filePath: string = '';
  private static pending: string = '';
  private static flushTimer: number = -1;
  private static readonly MAX_SIZE: number = 2 * 1024 * 1024; // 2MB 轮转

  /**
   * 初始化（在 Ability 的 aboutToAppear 中调用一次）
   */
  static init(context: Context): void {
    try {
      Logger.filePath = `${context.filesDir}/audiorelay.log`;
      Logger.file = fs.openSync(Logger.filePath,
        fs.OpenMode.READ_WRITE | fs.OpenMode.CREATE | fs.OpenMode.APPEND);
      console.info('[Logger] file log initialized');
    } catch (e) {
      console.error(`[Logger] init failed: ${JSON.stringify(e)}`);
    }
  }

  static info(tag: string, msg: string): void {
    Logger.write('I', tag, msg);
  }

  static warn(tag: string, msg: string): void {
    Logger.write('W', tag, msg);
  }

  static error(tag: string, msg: string): void {
    Logger.write('E', tag, msg);
  }

  private static write(level: string, tag: string, msg: string): void {
    const line = `${Logger.timestamp()} ${level} [${tag}] ${msg}\n`;
    // 控制台照常输出（hilog 实时查看用）
    if (level === 'E') {
      console.error(line.trim());
    } else {
      console.info(line.trim());
    }
    // 累积后批量刷盘，减少 IO
    Logger.pending += line;
    if (Logger.flushTimer === -1) {
      Logger.flushTimer = setTimeout(() => {
        Logger.flushTimer = -1;
        Logger.flush();
      }, 2000);
    }
  }

  private static flush(): void {
    if (Logger.pending.length === 0 || !Logger.file) {
      return;
    }
    try {
      // 轮转：超过上限则归档为 .1 后重建
      const stat = fs.statSync(Logger.filePath);
      if (stat.size > Logger.MAX_SIZE) {
        fs.closeSync(Logger.file.fd);
        try {
          fs.renameSync(Logger.filePath, `${Logger.filePath}.1`);
        } catch (e) {
          console.error(`[Logger] rotate rename failed: ${JSON.stringify(e)}`);
        }
        Logger.file = fs.openSync(Logger.filePath,
          fs.OpenMode.READ_WRITE | fs.OpenMode.CREATE | fs.OpenMode.APPEND);
      }
      fs.writeSync(Logger.file.fd, Logger.pending);
      Logger.pending = '';
    } catch (e) {
      console.error(`[Logger] flush failed: ${JSON.stringify(e)}`);
    }
  }

  private static timestamp(): string {
    const d = new Date();
    const p = (n: number, w: number = 2): string => String(n).padStart(w, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ` +
      `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
  }
}
