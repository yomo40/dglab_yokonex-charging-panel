/**
 * DG-LAB 波形生成器
 * 用于生成符合协议要求的波形数据
 */

import { WaveformData } from '../IDeviceAdapter';

/**
 * 单条波形参数
 */
export interface WaveformParams {
  frequency: number; // 频率 (10-1000)
  strength: number; // 强度 (0-100)
}

/**
 * 波形预设
 */
export interface WaveformPreset {
  name: string;
  description: string;
  params: WaveformParams[];
}

/**
 * 内置波形预设
 */
export const BuiltInPresets: Record<string, WaveformPreset> = {
  gentle: {
    name: '轻柔',
    description: '低频低强度，适合热身',
    params: [
      { frequency: 20, strength: 10 },
      { frequency: 25, strength: 15 },
      { frequency: 30, strength: 20 },
      { frequency: 25, strength: 15 },
    ],
  },
  pulse: {
    name: '脉冲',
    description: '规律的脉冲感',
    params: [
      { frequency: 50, strength: 30 },
      { frequency: 50, strength: 0 },
      { frequency: 50, strength: 30 },
      { frequency: 50, strength: 0 },
    ],
  },
  wave: {
    name: '波浪',
    description: '渐进的波浪感',
    params: [
      { frequency: 30, strength: 20 },
      { frequency: 40, strength: 40 },
      { frequency: 50, strength: 60 },
      { frequency: 40, strength: 40 },
    ],
  },
  intense: {
    name: '强烈',
    description: '高频高强度',
    params: [
      { frequency: 100, strength: 70 },
      { frequency: 120, strength: 80 },
      { frequency: 100, strength: 70 },
      { frequency: 80, strength: 60 },
    ],
  },
  random: {
    name: '随机',
    description: '随机变化的波形',
    params: [], // 动态生成
  },
};

/**
 * 波形生成器类
 */
export class WaveformGenerator {
  /**
   * 将用户输入的频率值(10-1000)转换为协议频率值(10-240)
   */
  static convertFrequency(inputFreq: number): number {
    if (inputFreq < 10) return 10;
    if (inputFreq > 1000) return 240;

    if (inputFreq <= 100) {
      return inputFreq;
    } else if (inputFreq <= 600) {
      return Math.floor((inputFreq - 100) / 5) + 100;
    } else {
      return Math.floor((inputFreq - 600) / 10) + 200;
    }
  }

  /**
   * 将协议频率值(10-240)转换回用户频率值(10-1000)
   */
  static revertFrequency(protocolFreq: number): number {
    if (protocolFreq < 10) return 10;
    if (protocolFreq > 240) return 1000;

    if (protocolFreq <= 100) {
      return protocolFreq;
    } else if (protocolFreq <= 200) {
      return (protocolFreq - 100) * 5 + 100;
    } else {
      return (protocolFreq - 200) * 10 + 600;
    }
  }

  /**
   * 生成单条波形的HEX数据
   * 每条波形包含: 频率(1byte) + 强度(1byte) = 2bytes = 4个HEX字符
   * 但根据协议，每条波形数据是8字节HEX (16个字符)
   * 格式: 频率高4位 + 强度高4位 + 频率低4位 + 强度低4位 (重复填充至8字节)
   */
  static generateSingleWaveformHex(frequency: number, strength: number): string {
    // 确保值在有效范围内
    const freq = Math.max(10, Math.min(240, this.convertFrequency(frequency)));
    const str = Math.max(0, Math.min(100, strength));

    // 转换为HEX
    const freqHex = freq.toString(16).padStart(2, '0').toUpperCase();
    const strHex = str.toString(16).padStart(2, '0').toUpperCase();

    // 组合成8字节(16字符)的HEX数据
    // 根据V3协议，波形数据格式: 频率 + 强度，重复4次组成16字符
    return (freqHex + strHex).repeat(4);
  }

  /**
   * 从WaveformData生成HEX数组
   * @param data 波形数据
   * @returns HEX字符串数组，每个元素代表100ms的数据
   */
  static generateHexArray(data: WaveformData): string[] {
    const hexArray: string[] = [];
    const length = Math.min(data.frequency.length, data.strength.length);

    // 每4组数据合并为一条100ms的波形数据
    for (let i = 0; i < length; i += 4) {
      let hexData = '';
      for (let j = 0; j < 4 && i + j < length; j++) {
        const freq = this.convertFrequency(data.frequency[i + j]);
        const str = data.strength[i + j];
        hexData += freq.toString(16).padStart(2, '0');
        hexData += str.toString(16).padStart(2, '0');
      }
      // 如果不足8字节，用最后一组数据填充
      while (hexData.length < 16) {
        hexData += hexData.slice(-4);
      }
      hexArray.push(hexData.toUpperCase());
    }

    return hexArray;
  }

  /**
   * 根据预设生成波形数据
   */
  static fromPreset(presetName: string, duration: number = 1): WaveformData {
    const preset = BuiltInPresets[presetName];
    if (!preset) {
      throw new Error(`Unknown preset: ${presetName}`);
    }

    let params = preset.params;

    // 随机预设特殊处理
    if (presetName === 'random') {
      params = this.generateRandomParams();
    }

    // 根据持续时间扩展数据
    const repeatCount = Math.ceil((duration * 10) / params.length); // 每100ms一条数据
    const frequency: number[] = [];
    const strength: number[] = [];

    for (let i = 0; i < repeatCount; i++) {
      for (const p of params) {
        frequency.push(p.frequency);
        strength.push(p.strength);
      }
    }

    return {
      frequency: frequency.slice(0, duration * 10),
      strength: strength.slice(0, duration * 10),
      duration,
    };
  }

  /**
   * 生成随机波形参数
   */
  static generateRandomParams(count: number = 4): WaveformParams[] {
    const params: WaveformParams[] = [];
    for (let i = 0; i < count; i++) {
      params.push({
        frequency: Math.floor(Math.random() * 90) + 10, // 10-100
        strength: Math.floor(Math.random() * 60) + 10, // 10-70
      });
    }
    return params;
  }

  /**
   * 生成渐变波形
   * @param startFreq 起始频率
   * @param endFreq 结束频率
   * @param startStrength 起始强度
   * @param endStrength 结束强度
   * @param steps 步数
   */
  static generateGradient(
    startFreq: number,
    endFreq: number,
    startStrength: number,
    endStrength: number,
    steps: number
  ): WaveformData {
    const frequency: number[] = [];
    const strength: number[] = [];

    for (let i = 0; i < steps; i++) {
      const t = i / (steps - 1);
      frequency.push(Math.round(startFreq + (endFreq - startFreq) * t));
      strength.push(Math.round(startStrength + (endStrength - startStrength) * t));
    }

    return { frequency, strength, duration: steps / 10 };
  }

  /**
   * 生成正弦波形
   * @param baseFreq 基础频率
   * @param freqAmplitude 频率振幅
   * @param baseStrength 基础强度
   * @param strAmplitude 强度振幅
   * @param cycles 周期数
   * @param pointsPerCycle 每周期采样点数
   */
  static generateSineWave(
    baseFreq: number,
    freqAmplitude: number,
    baseStrength: number,
    strAmplitude: number,
    cycles: number,
    pointsPerCycle: number = 10
  ): WaveformData {
    const frequency: number[] = [];
    const strength: number[] = [];
    const totalPoints = cycles * pointsPerCycle;

    for (let i = 0; i < totalPoints; i++) {
      const angle = (2 * Math.PI * i) / pointsPerCycle;
      frequency.push(Math.round(baseFreq + freqAmplitude * Math.sin(angle)));
      strength.push(Math.round(baseStrength + strAmplitude * Math.sin(angle)));
    }

    return { frequency, strength, duration: totalPoints / 10 };
  }

  /**
   * 合并多个波形数据
   */
  static concat(...waveforms: WaveformData[]): WaveformData {
    const frequency: number[] = [];
    const strength: number[] = [];
    let duration = 0;

    for (const wf of waveforms) {
      frequency.push(...wf.frequency);
      strength.push(...wf.strength);
      duration += wf.duration || wf.frequency.length / 10;
    }

    return { frequency, strength, duration };
  }
}
