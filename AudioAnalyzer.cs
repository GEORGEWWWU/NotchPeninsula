using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NotchPeninsula
{
    public class AudioAnalyzer
    {
        private WasapiLoopbackCapture? _capture;
        private float[] _frontBars = new float[5];
        private float[] _backBars = new float[5];
        private float[] _tempBars = new float[5];

        // 5个频率点对应的 Goertzel 递推状态
        private readonly (float coeff, float q0, float q1, float q2, float weight)[] _state = new (float, float, float, float, float)[5];
        private int _sampleCount = 0;
        private int _channels;
        private int _decimationFactor;
        // 用于 AGC 自动增益补偿的峰值追踪
        private float _currentPeak = 0.1f;

        public AudioAnalyzer()
        {
            StartCapture();
        }

        private void StartCapture()
        {
            try
            {
                _capture = new WasapiLoopbackCapture(); // 默认捕获系统主混音输出
                _channels = _capture.WaveFormat.Channels;

                // 核心优化：极速降采样 (Decimation)。比如 48000Hz -> 取每 6 个样本中的 1 个 = 8000Hz
                _decimationFactor = Math.Max(1, _capture.WaveFormat.SampleRate / 8000);
                int actualSampleRate = _capture.WaveFormat.SampleRate / _decimationFactor;

                // 重新挑选 5 个最具代表性的律动频段，并进行巨大的高频能量补偿
                // 1. 底鼓 (80Hz) 2. 军鼓/下盘 (250Hz) 3. 人声 (600Hz) 4. 乐器高频 (1500Hz) 5. 极高频/镲片 (3500Hz)
                float[] targetFreqs = { 80f, 250f, 600f, 1500f, 3500f };
                // 补偿倍率：频率越高，现实中能量越小，需要强制放大视觉效果
                float[] weights = { 1.0f, 1.8f, 2.8f, 4.5f, 6.5f };

                for (int i = 0; i < 5; i++)
                {
                    float freq = Math.Min(targetFreqs[i], actualSampleRate / 2.2f);
                    float k = MathF.Round(freq * 256f / actualSampleRate);
                    float coeff = 2f * MathF.Cos(2f * MathF.PI * k / 256f);
                    // 存入特定的权重
                    _state[i] = (coeff, 0, 0, 0, weights[i]);
                }

                _capture.DataAvailable += OnAudioData;
                _capture.RecordingStopped += (s, e) => {
                    // 设备被拔出时清零
                    Array.Clear(_backBars, 0, 5);
                    Interlocked.Exchange(ref _frontBars, _backBars);
                };
                _capture.StartRecording();
            }
            catch
            {
                // 无权限或无音频设备时静默，全 0 柱子输出
            }
        }

        private void OnAudioData(object? sender, WaveInEventArgs e)
        {
            // WaveBuffer 提供 Zero-Copy 的方式直接把 byte[] 强转读作 float[]
            var buffer = new WaveBuffer(e.Buffer);
            int floatCount = e.BytesRecorded / 4;

            // 跨步遍历，直接跳过不需要的高频样本
            for (int i = 0; i < floatCount; i += _channels * _decimationFactor)
            {
                float sample = buffer.FloatBuffer[i]; // 取单声道 (Left)

                // 跑 5 路 Goertzel
                for (int j = 0; j < 5; j++)
                {
                    ref var s = ref _state[j];
                    float q0 = sample + s.coeff * s.q1 - s.q2;
                    s.q2 = s.q1;
                    s.q1 = q0;
                }

                // 每满 256 个降采样后的样本（约 32ms），计算一次能量输出
                if (++_sampleCount >= 256)
                {
                    float maxValThisFrame = 0f;

                    for (int j = 0; j < 5; j++)
                    {
                        ref var s = ref _state[j];
                        // 计算原始能量
                        float power = s.q1 * s.q1 + s.q2 * s.q2 - s.coeff * s.q1 * s.q2;

                        // ★ 修改：先计算出未裁剪的原始 val，不急着 Clamp
                        float val = (MathF.Sqrt(Math.Max(0, power)) / 256f) * 20f * s.weight;
                        _tempBars[j] = val;

                        // 找出这 5 根柱子里的最大值
                        if (val > maxValThisFrame)
                            maxValThisFrame = val;

                        s.q1 = 0; s.q2 = 0; // 重置状态
                    }
                    _sampleCount = 0;

                    // ==========================================
                    // 🎛️ AGC 自动增益补偿核心逻辑
                    // ==========================================
                    // 1. 包络追踪 (Envelope Tracking)：快升慢降
                    if (maxValThisFrame > _currentPeak)
                        _currentPeak = maxValThisFrame; // 极速起跳 (Attack)：大音量瞬间压制，防爆音
                    else
                        _currentPeak *= 0.98f;          // 缓慢衰减 (Release)：音量减小时，倍率在1-2秒内优雅回升

                    // 2. 划定底噪红线，防止在纯静音（0音量）时产生除以零，或者把主板电流底噪无限放大
                    float safePeak = Math.Max(_currentPeak, 0.02f);

                    // 3. 计算动态倍率：期望这首歌的峰值能打到 0.85 的高度
                    // 限制最大放大倍数为 15 倍（即使系统音量只有 6%，也能看起来像 100%）
                    float dynamicGain = Math.Clamp(0.85f / safePeak, 1f, 15f);

                    // 4. 应用动态增益，并进行最终裁剪
                    for (int j = 0; j < 5; j++)
                    {
                        _tempBars[j] = Math.Clamp(_tempBars[j] * dynamicGain, 0f, 1f);
                    }
                    // ==========================================

                    // 无锁双缓冲原子交换
                    Array.Copy(_tempBars, _backBars, 5);
                    var oldFront = Interlocked.Exchange(ref _frontBars, _backBars);
                    Array.Clear(oldFront, 0, 5); // 回收并清空旧 Buffer
                    _backBars = oldFront;
                }
            }
        }

        public float[] GetBars() => _frontBars;
    }
}