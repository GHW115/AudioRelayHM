/*
 * AudioRelayHM — 独立测试页面
 * 运行 AudioPacket / NetworkParse / AdpcmDecoder 测试，直接显示结果
 */

import router from '@ohos.router';
import { AudioPacket, MessageType, ControlCommand, StreamDirection, EncodingType } from '../model/AudioPacket';
import { NetworkService } from '../service/NetworkService';
import { AdpcmDecoder } from '../service/AdpcmDecoder';

interface TestResult {
  name: string;
  passed: boolean;
  detail: string;
}

@Entry
@Component
struct TestPage {
  @State results: TestResult[] = [];
  @State totalPassed: number = 0;
  @State totalFailed: number = 0;
  @State running: boolean = true;

  aboutToAppear(): void {
    this.runAllTests();
  }

  runAllTests(): void {
    let res: TestResult[] = [];
    // AudioPacket tests
    res.push(this.test_RoundtripPcm());
    res.push(this.test_RoundtripOpus());
    res.push(this.test_EmptyPayload());
    res.push(this.test_ConfigControl());
    res.push(this.test_TimeSync());
    res.push(this.test_SerializedSize());
    res.push(this.test_CanaryPcm());
    res.push(this.test_CanaryConfig());
    res.push(this.test_CanaryTimeSync());
    res.push(this.test_Int64Endian());
    // NetworkParse tests
    res.push(this.test_SinglePacket());
    res.push(this.test_TwoPacketsStuck());
    res.push(this.test_HalfPacket());
    res.push(this.test_HalfThenRest());
    // AdpcmDecoder tests
    res.push(this.test_AdpcmDeterministic());
    res.push(this.test_AdpcmOutputSize());
    res.push(this.test_AdpcmShortRange());

    this.results = res;
    this.totalPassed = res.filter((r) => r.passed).length;
    this.totalFailed = res.filter((r) => !r.passed).length;
    this.running = false;
  }

  // === helpers ===
  private assert(cond: boolean, msg: string): [boolean, string] {
    return [cond, cond ? '' : msg];
  }

  private record(name: string, conds: [boolean, string][]): TestResult {
    let allPassed = true;
    let failDetail = '';
    for (let c of conds) {
      if (!c[0]) {
        allPassed = false;
        if (failDetail) failDetail += '; ';
        failDetail += c[1];
      }
    }
    return { name: name, passed: allPassed, detail: failDetail };
  }

  // === AudioPacket ===
  test_RoundtripPcm(): TestResult {
    try {
      let p = new AudioPacket();
      p.msgType = MessageType.AUDIO_DATA;
      p.direction = StreamDirection.PC_TO_PHONE;
      p.encodingType = EncodingType.PCM;
      p.sequence = 42;
      p.timestamp = 1000;
      p.encodeTimestamp = 1001;
      p.sendTimestamp = 1002;
      p.sampleRate = 48000;
      p.channels = 2;
      p.bitsPerSample = 16;
      p.payload = new ArrayBuffer(4);
      new Uint8Array(p.payload).set([0x01, 0x02, 0x03, 0x04]);
      let r = AudioPacket.deserialize(p.serialize());
      return this.record('AudioPacket Roundtrip PCM', [
        this.assert(r.msgType === MessageType.AUDIO_DATA, 'msgType'),
        this.assert(r.sequence === 42, 'sequence'),
        this.assert(r.timestamp === 1000, 'timestamp'),
        this.assert(r.sampleRate === 48000, 'sampleRate'),
        this.assert(r.channels === 2, 'channels')
      ]);
    } catch (e) { return { name: 'AudioPacket Roundtrip PCM', passed: false, detail: JSON.stringify(e) }; }
  }

  test_RoundtripOpus(): TestResult {
    try {
      let payload = new ArrayBuffer(256);
      let v = new Uint8Array(payload);
      for (let i = 0; i < 256; i++) v[i] = (i * 17 + 42) & 0xFF;
      let p = new AudioPacket();
      p.msgType = MessageType.AUDIO_DATA;
      p.direction = StreamDirection.PHONE_TO_PC;
      p.encodingType = EncodingType.OPUS;
      p.sequence = 999;
      p.timestamp = 0x7FFFFFFFFFFFFFFE;
      p.sampleRate = 16000;
      p.channels = 1;
      p.bitsPerSample = 16;
      p.payload = payload;
      let r = AudioPacket.deserialize(p.serialize());
      return this.record('AudioPacket Roundtrip Opus', [
        this.assert(r.encodingType === EncodingType.OPUS, 'encoding'),
        this.assert(r.sequence === 999, 'sequence'),
        this.assert(r.payload.byteLength === 256, 'payload len')
      ]);
    } catch (e) { return { name: 'AudioPacket Roundtrip Opus', passed: false, detail: JSON.stringify(e) }; }
  }

  test_EmptyPayload(): TestResult {
    try {
      let p = new AudioPacket();
      p.msgType = MessageType.AUDIO_DATA;
      p.direction = StreamDirection.PC_TO_PHONE;
      p.encodingType = EncodingType.PCM;
      p.payload = new ArrayBuffer(0);
      let r = AudioPacket.deserialize(p.serialize());
      return this.record('AudioPacket EmptyPayload', [
        this.assert(r.payload.byteLength === 0, 'payload empty')
      ]);
    } catch (e) { return { name: 'AudioPacket EmptyPayload', passed: false, detail: JSON.stringify(e) }; }
  }

  test_ConfigControl(): TestResult {
    try {
      let payload = new ArrayBuffer(9);
      let v = new DataView(payload);
      v.setUint8(0, 1);
      v.setInt32(1, 64, true);
      v.setInt32(5, 200, true);
      let p = new AudioPacket();
      p.msgType = MessageType.CONTROL;
      p.command = ControlCommand.CONFIG;
      p.sequence = 100;
      p.timestamp = 5000;
      p.payload = payload;
      let r = AudioPacket.deserialize(p.serialize());
      return this.record('AudioPacket CONFIG', [
        this.assert(r.command === ControlCommand.CONFIG, 'command'),
        this.assert(r.sequence === 100, 'sequence')
      ]);
    } catch (e) { return { name: 'AudioPacket CONFIG', passed: false, detail: JSON.stringify(e) }; }
  }

  test_TimeSync(): TestResult {
    try {
      let p = new AudioPacket();
      p.msgType = MessageType.CONTROL;
      p.command = ControlCommand.TIME_SYNC;
      p.timestamp = 0x12345678;
      p.payload = new ArrayBuffer(16);
      let r = AudioPacket.deserialize(p.serialize());
      return this.record('AudioPacket TIME_SYNC', [
        this.assert(r.command === ControlCommand.TIME_SYNC, 'command'),
        this.assert(r.payload.byteLength === 16, 'payload 16')
      ]);
    } catch (e) { return { name: 'AudioPacket TIME_SYNC', passed: false, detail: JSON.stringify(e) }; }
  }

  test_SerializedSize(): TestResult {
    try {
      for (let len of [0, 1, 100, 256, 1024]) {
        let p = new AudioPacket();
        p.msgType = MessageType.AUDIO_DATA;
        p.direction = StreamDirection.PC_TO_PHONE;
        p.encodingType = EncodingType.PCM;
        p.sequence = 1;
        p.timestamp = 1;
        p.encodeTimestamp = 1;
        p.sendTimestamp = 1;
        p.sampleRate = 48000;
        p.channels = 2;
        p.bitsPerSample = 16;
        p.payload = new ArrayBuffer(len);
        if (p.serialize().byteLength !== 44 + len) {
          return { name: 'AudioPacket SerializedSize', passed: false, detail: `len=${len}` };
        }
      }
      return { name: 'AudioPacket SerializedSize', passed: true, detail: '' };
    } catch (e) { return { name: 'AudioPacket SerializedSize', passed: false, detail: JSON.stringify(e) }; }
  }

  test_CanaryPcm(): TestResult {
    try {
      let bytes = new Uint8Array(44);
      bytes[0] = 0x01; bytes[1] = 0xFF; bytes[2] = 0x00; bytes[3] = 0x00;
      bytes[4] = 0x05; bytes[5] = 0x00; bytes[6] = 0x00; bytes[7] = 0x00;
      bytes[8] = 0xE8; bytes[9] = 0x03; bytes[32] = 0x80; bytes[33] = 0xBB;
      bytes[36] = 0x02; bytes[37] = 0x10;
      let p = AudioPacket.deserialize(bytes.buffer as ArrayBuffer);
      return this.record('Canary PCM AudioData', [
        this.assert(p.msgType === MessageType.AUDIO_DATA, 'msgType'),
        this.assert(p.sequence === 5, 'seq=5'),
        this.assert(p.sampleRate === 48000, 'sr=48000'),
        this.assert(p.channels === 2, 'ch=2')
      ]);
    } catch (e) { return { name: 'Canary PCM AudioData', passed: false, detail: JSON.stringify(e) }; }
  }

  test_CanaryConfig(): TestResult {
    try {
      let h = new Uint8Array(44);
      h[0] = 0x00; h[1] = 0x06; h[2] = 0xFF; h[3] = 0x01;
      h[4] = 0x07; h[5] = 0x00; h[6] = 0x00; h[7] = 0x00;
      h[8] = 0xD0; h[9] = 0x07;
      h[40] = 0x09; h[41] = 0x00; h[42] = 0x00; h[43] = 0x00;
      let pl = new Uint8Array(9);
      pl[0] = 0x01; pl[1] = 0x40; pl[5] = 0xC8;
      let full = new Uint8Array(53);
      full.set(h, 0); full.set(pl, 44);
      let p = AudioPacket.deserialize(full.buffer as ArrayBuffer);
      return this.record('Canary CONFIG', [
        this.assert(p.command === ControlCommand.CONFIG, 'command'),
        this.assert(p.sequence === 7, 'seq=7')
      ]);
    } catch (e) { return { name: 'Canary CONFIG', passed: false, detail: JSON.stringify(e) }; }
  }

  test_CanaryTimeSync(): TestResult {
    try {
      let h = new Uint8Array(44);
      h[0] = 0x00; h[1] = 0x08; h[2] = 0xFF;
      h[4] = 0x63; h[8] = 0x88; h[9] = 0x77; h[10] = 0x66; h[11] = 0x55;
      h[12] = 0x44; h[13] = 0x33; h[14] = 0x22; h[15] = 0x11;
      h[40] = 0x10;
      let pl = new Uint8Array(16);
      let full = new Uint8Array(60);
      full.set(h, 0); full.set(pl, 44);
      let p = AudioPacket.deserialize(full.buffer as ArrayBuffer);
      return this.record('Canary TIME_SYNC', [
        this.assert(p.command === ControlCommand.TIME_SYNC, 'command'),
        this.assert(p.sequence === 99, 'seq=99'),
        this.assert(p.timestamp === 0x1122334455667788, 'ts')
      ]);
    } catch (e) { return { name: 'Canary TIME_SYNC', passed: false, detail: JSON.stringify(e) }; }
  }

  test_Int64Endian(): TestResult {
    try {
      let vals = [0, 1, 0xFFFFFFFF, 0x100000000, 0x1FFFFFFFF];
      for (let ts of vals) {
        let p = new AudioPacket();
        p.msgType = MessageType.AUDIO_DATA;
        p.direction = StreamDirection.PHONE_TO_PC;
        p.encodingType = EncodingType.PCM;
        p.sequence = 1;
        p.timestamp = ts;
        p.encodeTimestamp = ts + 1;
        p.sendTimestamp = ts + 2;
        p.sampleRate = 48000;
        p.channels = 2;
        p.bitsPerSample = 16;
        p.payload = new ArrayBuffer(0);
        let r = AudioPacket.deserialize(p.serialize());
        if (r.timestamp !== ts) return { name: 'Int64 Endian', passed: false, detail: `ts=${ts}` };
      }
      return { name: 'Int64 Endian', passed: true, detail: '' };
    } catch (e) { return { name: 'Int64 Endian', passed: false, detail: JSON.stringify(e) }; }
  }

  // === NetworkParse ===
  test_SinglePacket(): TestResult {
    try {
      let net = new NetworkService();
      let count: number = 0;
      net.onAudioData = () => { count++; };
      let pkt = this.buildPacket(MessageType.AUDIO_DATA, StreamDirection.PC_TO_PHONE, EncodingType.PCM, 1, 100, 8);
      net.handleData(pkt.buffer as ArrayBuffer);
      return this.record('Parse Single Packet', [this.assert(count === 1, `count=${count}`)]);
    } catch (e) { return { name: 'Parse Single Packet', passed: false, detail: JSON.stringify(e) }; }
  }

  test_TwoPacketsStuck(): TestResult {
    try {
      let net = new NetworkService();
      let seqs: number[] = [];
      net.onAudioData = (p: AudioPacket) => { seqs.push(p.sequence); };
      let p1 = this.buildPacket(MessageType.AUDIO_DATA, StreamDirection.PC_TO_PHONE, EncodingType.PCM, 10, 200, 4);
      let p2 = this.buildPacket(MessageType.AUDIO_DATA, StreamDirection.PC_TO_PHONE, EncodingType.PCM, 11, 300, 4);
      let c = new Uint8Array(p1.length + p2.length);
      c.set(p1, 0); c.set(p2, p1.length);
      net.handleData(c.buffer as ArrayBuffer);
      return this.record('Parse Two Stuck', [
        this.assert(seqs.length === 2, `count=${seqs.length}`),
        this.assert(seqs[0] === 10, `seq0=${seqs[0]}`),
        this.assert(seqs[1] === 11, `seq1=${seqs[1]}`)
      ]);
    } catch (e) { return { name: 'Parse Two Stuck', passed: false, detail: JSON.stringify(e) }; }
  }

  test_HalfPacket(): TestResult {
    try {
      let net = new NetworkService();
      let count: number = 0;
      net.onAudioData = () => { count++; };
      net.handleData(new ArrayBuffer(20));
      return this.record('Parse Half Packet', [this.assert(count === 0, `count=${count}`)]);
    } catch (e) { return { name: 'Parse Half Packet', passed: false, detail: JSON.stringify(e) }; }
  }

  test_HalfThenRest(): TestResult {
    try {
      let net = new NetworkService();
      let count: number = 0;
      net.onAudioData = () => { count++; };
      let full = this.buildPacket(MessageType.AUDIO_DATA, StreamDirection.PC_TO_PHONE, EncodingType.PCM, 30, 500, 16, 0xCC);
      net.handleData(full.buffer.slice(0, 30));
      if (count !== 0) return { name: 'Parse HalfThenRest', passed: false, detail: 'half triggered' };
      net.handleData(full.buffer.slice(30));
      return this.record('Parse HalfThenRest', [this.assert(count > 0, `count=${count}`)]);
    } catch (e) { return { name: 'Parse HalfThenRest', passed: false, detail: JSON.stringify(e) }; }
  }

  private buildPacket(msgType: MessageType, cmdOrDir: number, enc: EncodingType, seq: number, ts: number, payloadLen: number, fillByte: number = 0): Uint8Array {
    let total = 44 + payloadLen;
    let bytes = new Uint8Array(total);
    bytes[0] = msgType;
    if (msgType === MessageType.AUDIO_DATA) { bytes[1] = 0xFF; bytes[2] = cmdOrDir & 0xFF; }
    else { bytes[1] = cmdOrDir & 0xFF; bytes[2] = 0xFF; }
    bytes[3] = enc;
    bytes[4] = seq & 0xFF; bytes[5] = (seq >> 8) & 0xFF;
    bytes[6] = (seq >> 16) & 0xFF; bytes[7] = (seq >> 24) & 0xFF;
    let tsLow = ts >>> 0;
    bytes[8] = tsLow & 0xFF; bytes[9] = (tsLow >> 8) & 0xFF;
    bytes[10] = (tsLow >> 16) & 0xFF; bytes[11] = (tsLow >> 24) & 0xFF;
    bytes[40] = payloadLen & 0xFF; bytes[41] = (payloadLen >> 8) & 0xFF;
    bytes[42] = (payloadLen >> 16) & 0xFF; bytes[43] = (payloadLen >> 24) & 0xFF;
    for (let i = 44; i < total; i++) bytes[i] = fillByte;
    return bytes;
  }

  // === AdpcmDecoder ===
  test_AdpcmDeterministic(): TestResult {
    try {
      let ch = 2; let hs = ch * 8; let dl = 100;
      let adpcm = new Uint8Array(hs + dl);
      for (let i = hs; i < adpcm.length; i++) adpcm[i] = (i * 7 + 13) & 0xFF;
      let r1 = AdpcmDecoder.decode(adpcm.buffer as ArrayBuffer, ch);
      let r2 = AdpcmDecoder.decode(adpcm.buffer as ArrayBuffer, ch);
      if (r1.byteLength !== r2.byteLength) return { name: 'Adpcm Deterministic', passed: false, detail: 'len diff' };
      let v1 = new Uint8Array(r1);
      let v2 = new Uint8Array(r2);
      for (let i = 0; i < v1.length; i++) { if (v1[i] !== v2[i]) return { name: 'Adpcm Deterministic', passed: false, detail: `byte ${i}` }; }
      return { name: 'Adpcm Deterministic', passed: true, detail: '' };
    } catch (e) { return { name: 'Adpcm Deterministic', passed: false, detail: JSON.stringify(e) }; }
  }

  test_AdpcmOutputSize(): TestResult {
    try {
      let ch = 2; let hs = ch * 8;
      for (let dl of [1, 10, 50, 100, 200]) {
        let adpcm = new Uint8Array(hs + dl);
        let r = AdpcmDecoder.decode(adpcm.buffer as ArrayBuffer, ch);
        if (r.byteLength !== dl * 4) return { name: 'Adpcm OutputSize', passed: false, detail: `dl=${dl} got=${r.byteLength}` };
      }
      return { name: 'Adpcm OutputSize', passed: true, detail: '' };
    } catch (e) { return { name: 'Adpcm OutputSize', passed: false, detail: JSON.stringify(e) }; }
  }

  test_AdpcmShortRange(): TestResult {
    try {
      let ch = 2; let hs = ch * 8; let dl = 200;
      let adpcm = new Uint8Array(hs + dl);
      for (let i = hs; i < adpcm.length; i++) adpcm[i] = (i % 3 === 0) ? 0x8F : 0x0F;
      let r = AdpcmDecoder.decode(adpcm.buffer as ArrayBuffer, ch);
      let v = new DataView(r);
      for (let i = 0; i < r.byteLength; i += 2) {
        let s = v.getInt16(i, true);
        if (s < -32768 || s > 32767) return { name: 'Adpcm ShortRange', passed: false, detail: `sample ${i}=${s}` };
      }
      return { name: 'Adpcm ShortRange', passed: true, detail: '' };
    } catch (e) { return { name: 'Adpcm ShortRange', passed: false, detail: JSON.stringify(e) }; }
  }

  build() {
    Column() {
      // Header
      Text('AudioRelayHM 测试结果')
        .fontSize(22).fontWeight(FontWeight.Bold)
        .margin({ top: 16, bottom: 8 })

      if (this.running) {
        LoadingProgress().width(40).height(40).margin(20)
        Text('运行中...').fontSize(14)
      } else {
        // Summary
        Row() {
          Text(`通过: ${this.totalPassed}`).fontSize(16).fontColor('#4CAF50').margin({ right: 16 })
          Text(`失败: ${this.totalFailed}`).fontSize(16).fontColor(this.totalFailed > 0 ? '#F44336' : '#999')
        }.margin({ bottom: 12 })

        // Results list
        List() {
          ForEach(this.results, (item: TestResult) => {
            ListItem() {
              Row() {
                Text(item.passed ? '✓' : '✗')
                  .fontSize(18).fontColor(item.passed ? '#4CAF50' : '#F44336')
                  .margin({ right: 8 })
                Column() {
                  Text(item.name).fontSize(14).fontWeight(FontWeight.Medium)
                  if (!item.passed && item.detail) {
                    Text(item.detail).fontSize(11).fontColor('#999').maxLines(2)
                  }
                }.alignItems(HorizontalAlign.Start).layoutWeight(1)
              }.width('100%').padding({ top: 8, bottom: 8 }).border({ width: { bottom: 0.5 }, color: '#E0E0E0' })
            }
          })
        }.layoutWeight(1).margin({ top: 8 })

        // Back button
        Button('返回').onClick(() => { router.back(); }).margin({ top: 12, bottom: 24 })
      }
    }
    .width('100%').height('100%')
    .padding({ left: 16, right: 16 })
    .backgroundColor('#FAFAFA')
  }
}
