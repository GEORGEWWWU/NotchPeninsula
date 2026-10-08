using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace NotchPeninsula
{
    public class AudioAnalyzer : IDisposable
    {
        private const int TargetSampleRate = 8000;
        // 看门狗健康检查周期
        private const int HealthCheckIntervalMs = 500;
        // 失效后重建捕获的最小间隔，避免长时间独占期间反复初始化
        private const int RestartAttemptIntervalMs = 1000;
        private const double DataSilenceTimeoutMs = 1500d;
        private const int DeviceProbeIntervalMs = 10000;

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

        // ---- 捕获生命周期 ----
        private readonly object _captureLock = new();
        private WasapiLoopbackCapture? _capture;
        private volatile bool _restartingCapture; // 本类主动释放旧捕获时，忽略其停止回调
        private long _lastDataTicks;              // 最后一次收到音频数据（含静音帧）的时间

        // ---- 默认输出设备跟踪 ----
        private volatile string _capturedDeviceId = "";
        private int _deviceChangeEpoch;
        private int _deviceCheckedEpoch;

        private readonly ManualResetEventSlim _checkSignal = new(false); // 事件催促看门狗立即复核
        private readonly CancellationTokenSource _shutdown = new();
        private readonly CancellationToken _shutdownToken; // 取自 _shutdown，CTS 被 Dispose 后仍可安全读取
        private Task? _watchdogTask;
        private volatile bool _disposed;

        // ---- Core Audio 事件订阅 ----
        private readonly NotificationClient _notificationClient;
        private readonly SessionEventsHandler _sessionEvents;
        private MMDeviceEnumerator? _enumerator;
        private AudioEndpointVolume? _endpointVolume; // 需持有引用，否则音量回调会被回收
        private AudioEndpointVolumeNotificationDelegate? _volumeNotificationHandler;
        private bool _systemEventsSubscribed;         // 订阅幂等守卫（见 SubscribeSystemEvents）
        private AudioSessionControl? _sessionControl; // 需持有引用，会话断开回调依赖它

        public AudioAnalyzer()
        {
            _shutdownToken = _shutdown.Token;
            _notificationClient = new NotificationClient(this);
            _sessionEvents = new SessionEventsHandler(this);

            SubscribeSystemEvents();

            TryStartCapture(); // 首次就被独占占用也没关系，看门狗会持续补救

            // 常驻看门狗：健康时只做极廉价的检查，失效时才重建捕获。
            _watchdogTask = Task.Factory.StartNew(WatchdogLoop, TaskCreationOptions.LongRunning);
        }

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

                if (_endpointVolume != null && _volumeNotificationHandler != null)
                {
                    try { _endpointVolume.OnVolumeNotification -= _volumeNotificationHandler; } catch { }
                }
                _volumeNotificationHandler = null;

                try { _enumerator?.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
                _enumerator = null;
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

        // ---- 捕获初始化 / 恢复 ----

        private bool TryStartCapture(bool isRetry = false)
        {
            lock (_captureLock)
            {
                if (_capture != null) return true;

                WasapiLoopbackCapture? capture = null;
                try
                {
                    // 先记下「现在」的默认输出设备，再构造捕获。
                    // （记的是新设备、绑的是旧设备，一比对反而「一致」）。
                    // 反过来先记录则只会偏旧，最多多重建一次，是安全的方向。
                    _capturedDeviceId = TryGetDefaultRenderDeviceId();
                    capture = new WasapiLoopbackCapture(); // 默认捕获系统主混音输出
                    ConfigureBands(capture.WaveFormat);

                    capture.DataAvailable += OnAudioData;
                    capture.RecordingStopped += OnRecordingStopped;
                    capture.StartRecording();
                }
                catch (Exception ex)
                {
                    if (isRetry) Logger.DebugThrottled("音频捕获仍未就绪（按退避重试中，同类消息已折叠）");
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
            catch (Exception)
            {
                Logger.DebugThrottled("释放音频捕获失败（同类消息已折叠）");
            }
            finally
            {
                _restartingCapture = false;
            }

            // 旧会话随捕获一并销毁：先摘掉事件订阅再丢引用。
            var session = _sessionControl;
            _sessionControl = null;
            if (session != null)
            {
                try { session.UnRegisterEventClient(_sessionEvents); } catch { }
            }
        }

        public void EnsureCaptureAlive() => RequestCheck();

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

            long silenceMs = (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastDataTicks)) / TimeSpan.TicksPerMillisecond;
            return silenceMs < DataSilenceTimeoutMs;
        }

        private bool IsCapturedDeviceStillDefault()
        {
            int epoch = Volatile.Read(ref _deviceChangeEpoch);
            if (epoch == _deviceCheckedEpoch) return true;

            string current = TryGetDefaultRenderDeviceId();
            Volatile.Write(ref _deviceCheckedEpoch, epoch);

            if (current.Length == 0) return true;

            return string.Equals(current, _capturedDeviceId, StringComparison.OrdinalIgnoreCase);
        }

        private string TryGetDefaultRenderDeviceId()
        {
            try
            {
                var enumerator = _enumerator;
                if (enumerator == null) return "";

                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return device.ID ?? "";
            }
            catch (Exception)
            {
                Logger.DebugThrottled("读取默认输出设备失败（同类消息已折叠）");
                return "";
            }
        }

        private void OnDefaultRenderDeviceChanged()
        {
            Interlocked.Increment(ref _deviceChangeEpoch);
            EnsureCaptureAlive();
        }

        private void WatchdogLoop()
        {
            bool recovering = false;
            long nextAttemptTicks = 0;
            long nextDeviceProbeTicks = DateTime.UtcNow.Ticks + DeviceProbeIntervalMs * TimeSpan.TicksPerMillisecond;

            while (!_shutdownToken.IsCancellationRequested)
            {
                try
                {
                    bool signaled = _checkSignal.Wait(HealthCheckIntervalMs);
                    _checkSignal.Reset();
                    if (_shutdownToken.IsCancellationRequested) break; // 退出前不再动捕获

                    if (DateTime.UtcNow.Ticks >= nextDeviceProbeTicks)
                    {
                        Interlocked.Increment(ref _deviceChangeEpoch);
                        nextDeviceProbeTicks = DateTime.UtcNow.Ticks + DeviceProbeIntervalMs * TimeSpan.TicksPerMillisecond;
                    }

                    bool deviceChanged = !IsCapturedDeviceStillDefault();

                    if (!deviceChanged && IsCaptureAlive())
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
                        Logger.Info(deviceChanged
                            ? "默认输出设备已切换，正在重建音频捕获"
                            : "音频捕获已断开，等待设备释放后自动恢复");
                    }

                    // 无事件催促时按最小间隔限速，避免独占期间反复重建捕获。
                    // 设备切换是低频的用户操作，不受限速约束，立即跟随。
                    if (!signaled && !deviceChanged && DateTime.UtcNow.Ticks < nextAttemptTicks) continue;

                    ReleaseCapture();
                    TryStartCapture(isRetry: true);
                    nextAttemptTicks = DateTime.UtcNow.Ticks + RestartAttemptIntervalMs * TimeSpan.TicksPerMillisecond;
                }
                catch (Exception ex)
                {
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

        // ---- Core Audio 事件订阅 ----

        private void SubscribeSystemEvents()
        {
            // 失败时不置位，保留"下次再试"的能力。
            if (_systemEventsSubscribed) return;

            try
            {
                var enumerator = new MMDeviceEnumerator();
                enumerator.RegisterEndpointNotificationCallback(_notificationClient);

                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var volume = device.AudioEndpointVolume;
                _volumeNotificationHandler = _ => EnsureCaptureAlive();
                volume.OnVolumeNotification += _volumeNotificationHandler;

                _enumerator = enumerator;
                _endpointVolume = volume;
                _systemEventsSubscribed = true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"音频事件订阅失败，将无法感知设备变化: {ex.Message}");
            }
        }

        private void RegisterSessionEvents()
        {
            try
            {
                var enumerator = _enumerator;
                if (enumerator == null) return;

                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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
                if (flow == DataFlow.Render) owner.OnDefaultRenderDeviceChanged();
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

        // ---- 频谱计算 ----

        private void ConfigureBands(WaveFormat format)
        {
            _channels = format.Channels;

            _decimationFactor = Math.Max(1, format.SampleRate / TargetSampleRate);
            int actualSampleRate = format.SampleRate / _decimationFactor;

            float[] targetFreqs = { 80f, 250f, 600f, 1500f, 3500f };
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

                    // AGC 自动增益补偿核心逻辑
                    if (maxValThisFrame > _currentPeak)
                        _currentPeak = maxValThisFrame; // 极速起跳 (Attack)：大音量瞬间压制，防爆音
                    else
                        _currentPeak *= 0.98f;          // 缓慢衰减 (Release)：音量减小时，倍率在1-2秒内优雅回升

                    float safePeak = Math.Max(_currentPeak, 0.02f);

                    // 3. 计算动态倍率：
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
