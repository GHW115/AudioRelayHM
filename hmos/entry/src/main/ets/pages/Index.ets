/**
 * 🤖 AI 辅助生成 — Claude (Anthropic)
 * 项目: AudioRelayHM 鸿蒙↔Windows 音频串流
 */

// pages/Index.ets - 重新设计 UI（深色音频工作室风格）

import { NetworkService, ConnectionState } from '../service/NetworkService';
import { AudioCapture, AudioCaptureCallback } from '../service/AudioCapture';
import { AudioPlay, AudioPlayCallback } from '../service/AudioPlay';
import { AudioPacket, StreamDirection } from '../model/AudioPacket';

@Entry
@Component
struct Index {
  @State private ipAddress: string = '192.168.2.232';
  @State private port: string = '9287';
  @State private connectionState: string = '未连接';
  @State private isConnected: boolean = false;
  @State private pcToPhoneActive: boolean = false;
  @State private phoneToPCActive: boolean = false;
  @State private statusText: string = '就绪';
  @State private encodingIndex: number = 2;
  @State private bufferIndex: number = 0;
  private encodingOptions: SelectOption[] = [
    { value: 'PCM' },
    { value: 'Opus 32k' }, { value: 'Opus 64k' },
    { value: 'Opus 128k' }, { value: 'Opus 192k' }
  ];
  private bufferOptions: SelectOption[] = [
    { value: '200ms' }, { value: '500ms' }, { value: '1000ms' }, { value: '2000ms' }
  ];
  @State private animScale: number = 1.0;

  private networkService: NetworkService = new NetworkService();
  private audioCapture: AudioCapture = new AudioCapture();
  private audioPlay: AudioPlay = new AudioPlay();

  aboutToAppear(): void {
    let ctx = AppStorage.get<Context>('abilityContext');
    if (ctx) {
      this.audioPlay.setContext(ctx);
    }
    this.setupCallbacks();
    // 脉冲动画
    setInterval(() => {
      this.animScale = this.animScale > 1.0 ? 0.8 : 1.0;
    }, 1500);
  }

  private setupCallbacks(): void {
    this.networkService.onStateChange = (state: ConnectionState) => {
      switch (state) {
        case ConnectionState.DISCONNECTED:
          this.connectionState = '未连接';
          this.isConnected = false;
          this.statusText = '已断开连接';
          this.pcToPhoneActive = false;
          this.phoneToPCActive = false;
          break;
        case ConnectionState.CONNECTING:
          this.connectionState = '连接中...';
          this.statusText = '正在连接 Windows 端...';
          break;
        case ConnectionState.CONNECTED:
          this.connectionState = '已连接';
          this.isConnected = true;
          this.statusText = '已连接到 Windows 端';
          break;
      }
    };

    this.networkService.onAudioData = (packet: AudioPacket) => {
      if (packet.direction === StreamDirection.PC_TO_PHONE) {
        this.audioPlay.writeData(packet.payload, packet.encodingType);
      }
    };

    const captureCallback: AudioCaptureCallback = new AudioCaptureCallback();
    captureCallback.onAudioData = (data: ArrayBuffer) => {
      this.networkService.sendAudioData(
        StreamDirection.PHONE_TO_PC,
        data,
        this.audioCapture.sampleRate,
        this.audioCapture.channels,
        this.encodingIndex
      );
    };
    captureCallback.onError = (error: string) => {
      this.statusText = `麦克风错误: ${error}`;
    };
    this.audioCapture.setCallback(captureCallback);

    const playCallback: AudioPlayCallback = new AudioPlayCallback();
    playCallback.onError = (error: string) => {
      this.statusText = `播放错误: ${error}`;
    };
    playCallback.onInfo = (info: string) => {
      this.statusText = info;
    };
    this.audioPlay.setCallback(playCallback);

    this.networkService.onError = (error: string) => {
      this.statusText = `连接错误: ${error}`;
    };
  }

  build() {
    Column() {
      // ===== 顶部标题区 =====
      Column() {
        // 音频波形图标
        Row() {
          ForEach([0, 1, 2, 3, 4], (i: number) => {
            Column()
              .width(4)
              .height(20 + (i % 2 === 0 ? 16 : 0))
              .backgroundColor(this.isConnected ? '#2563EB' : '#CBD5E1')
              .borderRadius(2)
              .margin({ left: 3, right: 3 })
              .animation({ duration: 600, curve: Curve.EaseInOut })
          })
        }
        .width(60)
        .height(40)
        .justifyContent(FlexAlign.Center)
        .margin({ bottom: 8 })

        Text('AudioRelay')
          .fontSize(26)
          .fontWeight(FontWeight.Bold)
          .fontColor('#1A202C')
          .letterSpacing(2)

        Text('鸿蒙 ⇄ Windows 音频串流')
          .fontSize(12)
          .fontColor('#A0AEC0')
          .margin({ top: 4 })
      }
      .width('100%')
      .padding({ top: 36, bottom: 20 })

      // ===== 连接设置卡片 =====
      Column() {
        Row() {
          Circle()
            .width(8).height(8)
            .fill(this.isConnected ? '#10B981' : '#EF4444')
            .margin({ right: 6 })
          Text('连接设置')
            .fontSize(15)
            .fontWeight(FontWeight.Medium)
            .fontColor('#2D3748')
        }
        .width('100%')
        .margin({ bottom: 16 })

        // IP 输入
        Row() {
          Text('IP')
            .fontSize(13)
            .fontColor('#718096')
            .width(30)
          TextInput({ placeholder: '请输入 Windows IP', text: this.ipAddress })
            .width('100%')
            .layoutWeight(1)
            .height(40)
            .backgroundColor('#F0F2F8')
            .borderRadius(10)
            .fontColor('#1A202C')
            .placeholderColor('#A0AEC0')
            .onChange((value: string) => { this.ipAddress = value; })
        }
        .width('100%')
        .margin({ bottom: 10 })

        // 端口输入 + 连接按钮
        Row() {
          Text('端口')
            .fontSize(13)
            .fontColor('#718096')
            .width(30)
          TextInput({ placeholder: '9287', text: this.port })
            .width(80)
            .height(40)
            .backgroundColor('#F0F2F8')
            .borderRadius(10)
            .fontColor('#1A202C')
            .placeholderColor('#A0AEC0')
            .onChange((value: string) => { this.port = value; })

          Blank()

          Button(this.isConnected ? '断开' : '连接')
            .width(100)
            .height(40)
            .type(ButtonType.Capsule)
            .fontColor(Color.White)
            .fontSize(15)
            .fontWeight(FontWeight.Bold)
            .backgroundColor(this.isConnected ? '#EF4444' : '#2563EB')
            .shadow({ radius: 8, color: this.isConnected ? '#EF444466' : '#2563EB55', offsetY: 3 })
            .onClick(() => {
              if (this.isConnected) {
                this.disconnectAll();
              } else {
                this.doConnect();
              }
            })
        }
        .width('100%')
        .alignItems(VerticalAlign.Center)
      }
      .width('90%')
      .padding(18)
      .backgroundColor('#FFFFFF')
      .borderRadius(16)
      .margin({ bottom: 12 })

      // ===== 连接状态 =====
      Row() {
        Circle()
          .width(10).height(10)
          .fill(this.isConnected ? '#10B981' : this.connectionState === '连接中...' ? '#F59E0B' : '#EF4444')
          .margin({ right: 8 })
          .shadow({
            radius: this.isConnected ? 8 : 0,
            color: this.isConnected ? '#10B98144' : Color.Transparent,
            offsetY: 0
          })
        Text(`状态: ${this.connectionState}`)
          .fontSize(13)
          .fontColor(this.isConnected ? '#10B981' : '#718096')
      }
      .width('90%')
      .padding({ top: 4, bottom: 12 })

      // ===== 音频控制卡片 =====
      Column() {
        Text('音频流控制')
          .fontSize(15)
          .fontWeight(FontWeight.Medium)
          .fontColor('#2D3748')
          .width('100%')
          .margin({ bottom: 16 })

        // PC → 手机
        Column() {
          Row() {
            // 图标
            Text('💻')
              .fontSize(20)
              .margin({ right: 10 })
            Column() {
              Text('PC → 手机')
                .fontSize(15)
                .fontWeight(FontWeight.Bold)
                .fontColor(this.pcToPhoneActive ? '#2563EB' : '#4A5568')
              Text('将电脑声音传到手机播放')
                .fontSize(11)
                .fontColor('#A0AEC0')
                .margin({ top: 2 })
            }
            .layoutWeight(1)

            Toggle({ type: ToggleType.Switch, isOn: this.pcToPhoneActive })
              .selectedColor('#2563EB')
              .switchPointColor('#FFFFFF')
              .onChange((isOn: boolean) => {
                this.pcToPhoneActive = isOn;
                this.togglePcToPhone(isOn);
              })
          }
          .width('100%')
          .padding(14)
          .backgroundColor(this.pcToPhoneActive ? '#2563EB11' : '#F0F2F8')
          .borderRadius(12)
          .border({ width: 1, color: this.pcToPhoneActive ? '#2563EB44' : '#E2E8F0' })
        }
        .width('100%')
        .margin({ bottom: 10 })

        // 手机 → PC
        Column() {
          Row() {
            Text('📱')
              .fontSize(20)
              .margin({ right: 10 })
            Column() {
              Text('手机 → PC')
                .fontSize(15)
                .fontWeight(FontWeight.Bold)
                .fontColor(this.phoneToPCActive ? '#10B981' : '#4A5568')
              Text('将手机麦克风声音传到电脑')
                .fontSize(11)
                .fontColor('#A0AEC0')
                .margin({ top: 2 })
            }
            .layoutWeight(1)

            Toggle({ type: ToggleType.Switch, isOn: this.phoneToPCActive })
              .selectedColor('#10B981')
              .switchPointColor('#FFFFFF')
              .onChange((isOn: boolean) => {
                this.phoneToPCActive = isOn;
                this.togglePhoneToPc(isOn);
              })
          }
          .width('100%')
          .padding(14)
          .backgroundColor(this.phoneToPCActive ? '#10B98111' : '#F0F2F8')
          .borderRadius(12)
          .border({ width: 1, color: this.phoneToPCActive ? '#10B98144' : '#E2E8F0' })
        }
        .width('100%')
      }
      .width('90%')
      .padding(18)
      .backgroundColor('#FFFFFF')
      .borderRadius(16)
      .margin({ bottom: 12 })

      // ===== 设置卡片 =====
      Column() {
        Row() {
          Column() {
            Text('编码方式')
              .fontSize(13)
              .fontColor('#4A5568')
              .margin({ bottom: 6 })
            Select(this.encodingOptions)
              .selected(this.encodingIndex)
              .value(this.encodingOptions[this.encodingIndex].value)
              .onSelect((index: number) => { this.encodingIndex = index; })
          }
          .layoutWeight(1)
          .margin({ right: 8 })

          Column() {
            Text('缓冲时间')
              .fontSize(13)
              .fontColor('#4A5568')
              .margin({ bottom: 6 })
            Select(this.bufferOptions)
              .selected(this.bufferIndex)
              .value(this.bufferOptions[this.bufferIndex].value)
              .onSelect((index: number) => { this.bufferIndex = index; })
          }
          .layoutWeight(1)
          .margin({ left: 8 })
        }
        .width('100%')
      }
      .width('90%')
      .padding(18)
      .backgroundColor('#FFFFFF')
      .borderRadius(16)
      .margin({ bottom: 12 })

      // ===== 底部状态 =====
      Text(this.statusText)
        .fontSize(12)
        .fontColor('#718096')
        .width('90%')
        .textAlign(TextAlign.Center)
        .margin({ top: 4 })
    }
    .width('100%')
    .height('100%')
    .backgroundColor('#F5F7FA')
  }

  private async doConnect(): Promise<void> {
    this.statusText = '正在连接...';
    try {
      await this.networkService.connect(this.ipAddress, parseInt(this.port));
    } catch (err) {
      this.statusText = '连接失败';
    }
  }

  private async disconnectAll(): Promise<void> {
    await this.audioCapture.stop();
    await this.audioPlay.stop();
    await this.networkService.disconnect();
    this.pcToPhoneActive = false;
    this.phoneToPCActive = false;
    this.statusText = '已断开';
  }

  private async togglePcToPhone(enable: boolean): Promise<void> {
    if (!this.isConnected) { this.statusText = '请先连接 Windows 端'; return; }
    if (enable) {
      await this.audioPlay.start();
      this.statusText = 'PC 音频 → 手机 已开启';
    } else {
      await this.audioPlay.stop();
      this.statusText = 'PC 音频 → 手机 已关闭';
    }
  }

  private async togglePhoneToPc(enable: boolean): Promise<void> {
    if (!this.isConnected) { this.statusText = '请先连接 Windows 端'; return; }
    if (enable) {
      await this.audioCapture.start();
      this.statusText = '手机麦克风 → PC 已开启';
    } else {
      this.audioCapture.stop();
      this.statusText = '手机麦克风 → PC 已关闭';
    }
  }
}
