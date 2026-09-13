using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace NotchPeninsula
{
    /// <summary>
    /// 系统输出（WASAPI Loopback）实时频谱分析。
    /// 其它程序以独占模式占用输出设备时，捕获流会被系统断开；这里用常驻看门狗轮询做健康检查
    /// （单次约 0.04µs，基本免费）并在判定失效时限速重建捕获（单次约 7ms），同时订阅 Core Audio
    /// 事件在音频环境变化时立即复核，因此恢复延迟通常在 1 个检查周期内。
    /// </summary>
    public class AudioAnalyzer : IDisposable
    {
        // 降采样后的目标采样率，Goertzel 只需在这个低采样率上跑
        private const int TargetSampleRate = 8000;
        // 看门狗健康检查周期
        private const int HealthCheckIntervalMs = 500;
        // 失效后重建捕获的最小间隔，避免长时间独占期间反复初始化
        private const int RestartAttemptIntervalMs = 1000;
        // Loopback 捕获即使静音也会持续送帧，超过该时长没有数据即认为流已失效
        private const double DataSilenceTimeoutMs = 1500d;

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

        // ===== 捕获生命周期 =====
        private readonly object _captureLock = new();
        private WasapiLoopbackCapture? _capture;
        private volatile bool _restartingCapture; // 本类主动释放旧捕获时，忽略其停止回调
        private long _lastDataTicks;              // 最后一次收到音频数据（含静音帧）的时间

        private readonly ManualResetEventSlim _checkSignal = new(false); // 事件催促看门狗立即复核
        private readonly CancellationTokenSource _shutdown = new();
        private readonly CancellationToken _shutdownToken; // 取自 _shutdown，CTS 被 Dispose 后仍可安全读取
        private Task? _watchdogTask;
        private volatile bool _disposed;

        // ===== Core Audio 事件订阅 =====
        private readonly NotificationClient _notificationClient;
        private readonly SessionEventsHandler _sessionEvents;
        private MMDeviceEnumerator? _enumerator;
        private AudioEndpointVolume? _endpointVolume; // 需持有引用，否则音量回调会被回收
        private AudioSessionControl? _sessionControl; // 需持有引用，会话断开回调依赖它

        public AudioAnalyzer()
        {
            _shutdownToken = _shutdown.Token;
            _notificationClient = new NotificationClient(this);
            _sessionEvents = new SessionEventsHandler(this);

            SubscribeSystemEvents();

            TryStartCapture(); // 首次就被独占占用也没关系，看门狗会持续补救

            // 常驻看门狗：健康时只做极廉价的检查，失效时才重建捕获。
            // 由 Dispose()（程序退出流程）通过 CancellationToken 停止。
            _watchdogTask = Task.Factory.StartNew(WatchdogLoop, TaskCreationOptions.LongRunning);
            _watchdogTask.ContinueWith(
                t => Logger.Error("音频分析看门狗任务异常退出", t.Exception?.InnerException ?? t.Exception),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        /// <summary>
        /// 停止看门狗并释放捕获与 Core Audio 订阅。供程序退出流程调用，可重复调用。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _shutdown.Cancel();
                _checkSignal.Set();        // 立即唤醒看门狗，让它看到取消
                _watchdogTask?.Wait(1000); // 等它退出，避免与下面的释放动作并发

                try { _sessionControl?.UnRegisterEventClient(_sessionEvents); } catch { }
                _sessionControl = null;
                ReleaseCapture();

                try { _enumerator?.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
                _endpointVolume?.Dispose();
                _endpointVolume = null;

                _checkSignal.Dispose();
                _shutdown.Dispose();
                Logger.Info("音频分析器已释放");
            }
            catch (Exception ex)
            {
                Logger.Error("音频分析器释放资源异常", ex);
            }
        }

        // ===================== 捕获初始化 / 恢复 =====================

        private bool TryStartCapture(bool isRetry = false)
        {
            lock (_captureLock)
            {
                if (_capture != null) return true;

                WasapiLoopbackCapture? capture = null;
                try
                {
                    capture = new WasapiLoopbackCapture(); // 默认捕获系统主混音输出
                    ConfigureBands(capture.WaveFormat);

                    capture.DataAvailable += OnAudioData;
                    capture.RecordingStopped += OnRecordingStopped;
                    capture.StartRecording();
                }
                catch (Exception ex)
                {
                    // 独占占用期间会按退避反复重试，重试失败只记 Debug，避免刷屏
                    if (isRetry) Logger.Debug($"音频捕获仍未就绪: {ex.Message}");
                    else Logger.Error("音频捕获初始化失败，可能被独占占用或无音频设备", ex);

                    try { capture?.Dispose(); } catch { } // 失败时释放半成品，避免退避重试泄漏
                    return false;
                }

                _capture = capture;
                Volatile.Write(ref _lastDataTicks, DateTime.UtcNow.Ticks);
                RegisterSessionEvents(); // 订阅本进程捕获会话的断开事件
                return true;
            }
        }

        /// <summary>释放旧捕获（幂等），供恢复流程重新初始化。</summary>
        private void ReleaseCapture()
        {
            WasapiLoopbackCapture? capture;
            lock (_captureLock)
            {
                capture = _capture;
                _capture = null;
            }
            if (capture == null) return;

            _restartingCapture = true;
            try
            {
                capture.DataAvailable -= OnAudioData;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Debug($"释放音频捕获失败: {ex.Message}");
            }
            finally
            {
                _restartingCapture = false;
            }

            _sessionControl = null; // 旧会话随捕获一并销毁
        }

        /// <summary>
        /// 由 Core Audio 事件回调或渲染层调用：请求立即复核捕获状态。
        /// 释放/重建固定在看门狗线程上执行，避免在 COM 回调线程里做耗时操作。
        /// </summary>
        public void EnsureCaptureAlive() => RequestCheck();

        /// <summary>
        /// 唤醒看门狗；Dispose 之后（进程正在退出）静默忽略，避免唤起已释放的同步原语——
        /// 调用方有渲染线程与 COM 回调线程，异常外逸会终止进程。
        /// </summary>
        private void RequestCheck()
        {
            if (_disposed) return;

            try
            {
                _checkSignal.Set();
            }
            catch (ObjectDisposedException)
            {
                // 与 Dispose 竞争，进程正在退出，忽略即可
            }
        }

        private bool IsCaptureAlive()
        {
            var capture = _capture;
            if (capture == null) return false;
            if (capture.CaptureState is CaptureState.Stopped or CaptureState.Stopping) return false;

            // Loopback 持续送帧（静音时送静音帧），长时间收不到数据说明流已被系统悄悄断开
            long silenceMs = (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastDataTicks)) / TimeSpan.TicksPerMillisecond;
            return silenceMs < DataSilenceTimeoutMs;
        }

        /// <summary>
        /// 常驻看门狗：健康时每 500ms 做一次约 0.04µs 的检查；判定失效后重建捕获（约 7ms/次），
        /// 重建之间至少间隔 2s；被 Core Audio 事件唤醒时跳过限速，立即重建。
        /// </summary>
        private void WatchdogLoop()
        {
            bool recovering = false;
            long nextAttemptTicks = 0;

            while (!_shutdownToken.IsCancellationRequested)
            {
                try
                {
                    bool signaled = _checkSignal.Wait(HealthCheckIntervalMs);
                    _checkSignal.Reset();
                    if (_shutdownToken.IsCancellationRequested) break; // 退出前不再动捕获

                    if (IsCaptureAlive())
                    {
                        if (recovering)
                        {
                            recovering = false;
                            Logger.Info("音频捕获已恢复");
                        }
                        continue;
                    }

                    if (!recovering)
                    {
                        recovering = true;
                        Logger.Info("音频捕获已断开，等待设备释放后自动恢复");
                    }

                    // 无事件催促时按最小间隔限速，避免独占期间反复重建捕获
                    if (!signaled && DateTime.UtcNow.Ticks < nextAttemptTicks) continue;

                    ReleaseCapture();
                    TryStartCapture(isRetry: true);
                    nextAttemptTicks = DateTime.UtcNow.Ticks + RestartAttemptIntervalMs * TimeSpan.TicksPerMillisecond;
                }
                catch (Exception ex)
                {
                    // 单轮异常绝不能让看门狗退出：Task 内的未观察异常会被静默吞掉，
                    // 看门狗一死频谱就永久失效且无人知晓。丢弃当前捕获后继续下一轮。
                    Logger.Error("音频看门狗检查异常，已丢弃当前捕获并继续运行", ex);
                    ReleaseCapture();
                    Thread.Sleep(HealthCheckIntervalMs); // 异常持续时避免变成紧循环
                }
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (_restartingCapture) return; // 主动重启导致的停止，不算故障

            if (e.Exception != null)
                Logger.Warn($"音频捕获意外停止: {e.Exception.Message}");
            else
                Logger.Warn("音频捕获已停止，等待恢复");

            ClearBars();
            RequestCheck(); // 释放与重启交给看门狗线程，避免在捕获线程里做耗时操作
        }

        // ===================== Core Audio 事件订阅 =====================

        private void SubscribeSystemEvents()
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                enumerator.RegisterEndpointNotificationCallback(_notificationClient);

                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var volume = device.AudioEndpointVolume;
                volume.OnVolumeNotification += _ => EnsureCaptureAlive();

                _enumerator = enumerator;
                _endpointVolume = volume;
            }
            catch (Exception ex)
            {
                Logger.Warn($"音频事件订阅失败，将无法感知设备变化: {ex.Message}");
            }
        }

        /// <summary>找到本进程在默认输出设备上的捕获会话，订阅其断开事件（独占抢占时会通知）。</summary>
        private void RegisterSessionEvents()
        {
            try
            {
                var enumerator = _enumerator;
                if (enumerator == null) return;

                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;
                uint pid = (uint)Environment.ProcessId;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.GetProcessID != pid) continue;

                    session.RegisterEventClient(_sessionEvents);
                    _sessionControl = session;
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"音频会话事件订阅失败: {ex.Message}");
            }
        }

        private void OnSessionDisconnected(AudioSessionDisconnectReason reason)
        {
            // 独占抢占 / 格式变更 / 设备移除 等都会走到这里，说明当前捕获已经作废
            Logger.Info($"音频会话已断开({reason})，等待设备释放后恢复");
            ClearBars();
            RequestCheck();
        }

        private sealed class NotificationClient(AudioAnalyzer owner) : IMMNotificationClient
        {
            public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.EnsureCaptureAlive();
            public void OnDeviceAdded(string deviceId) => owner.EnsureCaptureAlive();
            public void OnDeviceRemoved(string deviceId) => owner.EnsureCaptureAlive();
            public void OnPropertyValueChanged(string deviceId, PropertyKey key) => owner.EnsureCaptureAlive();
            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render) owner.EnsureCaptureAlive();
            }
        }

        private sealed class SessionEventsHandler(AudioAnalyzer owner) : IAudioSessionEventsHandler
        {
            public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => owner.OnSessionDisconnected(disconnectReason);
            public void OnStateChanged(AudioSessionState state) { }
            public void OnVolumeChanged(float volume, bool isMuted) { }
            public void OnDisplayNameChanged(string displayName) { }
            public void OnIconPathChanged(string iconPath) { }
            public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
            public void OnGroupingParamChanged(ref Guid groupingId) { }
        }

        // ===================== 频谱计算 =====================

        private void ConfigureBands(WaveFormat format)
        {
            _channels = format.Channels;

            // 核心优化：极速降采样 (Decimation)。比如 48000Hz -> 取每 6 个样本中的 1 个 = 8000Hz
            _decimationFactor = Math.Max(1, format.SampleRate / TargetSampleRate);
            int actualSampleRate = format.SampleRate / _decimationFactor;

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

            _sampleCount = 0;
            _currentPeak = 0.1f;
        }

        private void OnAudioData(object? sender, WaveInEventArgs e)
        {
            Volatile.Write(ref _lastDataTicks, DateTime.UtcNow.Ticks);

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

                        // 先计算出未裁剪的原始 val，不急着 Clamp
                        float val = (MathF.Sqrt(Math.Max(0, power)) / 256f) * 20f * s.weight;
                        _tempBars[j] = val;

                        // 找出这 5 根柱子里的最大值
                        if (val > maxValThisFrame)
                            maxValThisFrame = val;

                        s.q1 = 0; s.q2 = 0; // 重置状态
                    }
                    _sampleCount = 0;

                    // 🎛️ AGC 自动增益补偿核心逻辑
                    // 1. 包络追踪 (Envelope Tracking)：快升慢降
                    if (maxValThisFrame > _currentPeak)
                        _currentPeak = maxValThisFrame; // 极速起跳 (Attack)：大音量瞬间压制，防爆音
                    else
                        _currentPeak *= 0.98f;          // 缓慢衰减 (Release)：音量减小时，倍率在1-2秒内优雅回升

                    // 2. 划定底噪红线，防止在纯静音（0音量）时产生除以零，或者把主板电流底噪无限放大
                    float safePeak = Math.Max(_currentPeak, 0.02f);

                    // 3. 计算动态倍率：
                    // 将最大放大倍数从 15f 提升到 25f，并将目标高度微调到 0.9f
                    float dynamicGain = Math.Clamp(0.9f / safePeak, 1f, 30f);

                    // 4. 应用动态增益，并进行最终裁剪
                    for (int j = 0; j < 5; j++)
                    {
                        _tempBars[j] = Math.Clamp(_tempBars[j] * dynamicGain, 0f, 1f);
                    }

                    // 无锁双缓冲原子交换
                    Array.Copy(_tempBars, _backBars, 5);
                    var oldFront = Interlocked.Exchange(ref _frontBars, _backBars);
                    Array.Clear(oldFront, 0, 5); // 回收并清空旧 Buffer
                    _backBars = oldFront;
                }
            }
        }

        private void ClearBars()
        {
            Array.Clear(_backBars, 0, 5);
            var oldFront = Interlocked.Exchange(ref _frontBars, _backBars);
            Array.Clear(oldFront, 0, 5);
            _backBars = oldFront;
        }

        public float[] GetBars() => _frontBars;
    }
}
