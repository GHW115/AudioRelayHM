/**
 * 🤖 AI 辅助生成 — DeepSeek V4
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// service/BackgroundTaskGuard.ets
// 后台长时任务引用计数管理器
//
// 背景: startBackgroundRunning(context, mode, wantAgent) 按模式启动，
// 但 stopBackgroundRunning(context) 是全局的——会停止该应用的所有长时任务。
// 多个调用方（连接保活 DATA_TRANSFER / 播放 AUDIO_PLAYBACK / 录音 AUDIO_RECORDING）
// 若各自启动/停止，会出现"停止播放时把连接保活也连带停掉"的 bug（锁屏后进程被回收）。
//
// 方案: 全部调用方共享同一个 DATA_TRANSFER 任务实例，引用计数管理：
//   acquire(): 计数+1；首个调用方真正启动任务（后续仅计数）
//   release(): 计数-1；归零才调用 stopBackgroundRunning
// 统一使用 DATA_TRANSFER 模式：校验条件宽松（不要求真实播放/录音），
// 通知栏显示"数据传输中"常驻通知，达到保活目的。

import { backgroundTaskManager } from '@kit.BackgroundTasksKit';
import { wantAgent, WantAgent } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { Logger } from './Logger';


export class BackgroundTaskGuard {
  private static refCount: number = 0;
  private static running: boolean = false;
  private static context: Context | null = null;
  private static wantAgentObj: WantAgent | null = null;

  /**
   * 获取一个后台任务引用（首个调用方负责真正启动）
   * @returns 是否成功（失败时已回滚计数）
   */
  static async acquire(context: Context): Promise<boolean> {
    BackgroundTaskGuard.refCount++;
    if (BackgroundTaskGuard.running) {
      return true; // 任务已在运行，仅计数
    }
    try {
      const wantAgentInfo: wantAgent.WantAgentInfo = {
        wants: [
          {
            bundleName: context.applicationInfo.name,
            abilityName: 'EntryAbility',
          }
        ],
        actionType: wantAgent.OperationType.START_ABILITY,
        requestCode: 0,
        wantAgentFlags: [wantAgent.WantAgentFlags.UPDATE_PRESENT_FLAG],
      };
      BackgroundTaskGuard.wantAgentObj = await wantAgent.getWantAgent(wantAgentInfo);
      await backgroundTaskManager.startBackgroundRunning(
        context,
        backgroundTaskManager.BackgroundMode.DATA_TRANSFER,
        BackgroundTaskGuard.wantAgentObj);
      BackgroundTaskGuard.running = true;
      BackgroundTaskGuard.context = context;
      Logger.info('BackgroundTaskGuard', '后台保活任务已启动 (DATA_TRANSFER)');
      return true;
    } catch (err) {
      BackgroundTaskGuard.refCount--; // 启动失败，回滚计数
      let error = err as BusinessError;
      Logger.error('BackgroundTaskGuard', `启动失败: code=${error.code}, ${error.message}`);
      return false;
    }
  }

  /**
   * 释放一个后台任务引用（归零才真正停止，避免连带停掉其他调用方的保活）
   */
  static async release(): Promise<void> {
    if (BackgroundTaskGuard.refCount > 0) {
      BackgroundTaskGuard.refCount--;
    }
    if (BackgroundTaskGuard.refCount === 0 && BackgroundTaskGuard.running) {
      try {
        await backgroundTaskManager.stopBackgroundRunning(BackgroundTaskGuard.context);
        Logger.info('BackgroundTaskGuard', '后台保活任务已停止');
      } catch (err) {
        let error = err as BusinessError;
        Logger.error('BackgroundTaskGuard', `停止失败: code=${error.code}, ${error.message}`);
      }
      // 无论成败都复位标志：锁屏冻结期间 stop 的 IPC 会失败，若不复位，
      // 自动重连后 acquire() 会误以为任务仍在运行而不再重新启动，保活永久失效
      BackgroundTaskGuard.running = false;
      BackgroundTaskGuard.context = null;
    }
  }
}
