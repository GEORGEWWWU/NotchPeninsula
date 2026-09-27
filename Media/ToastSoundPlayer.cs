using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NotchPeninsula;

/// <summary>
/// 🎵 通知提示音播放器（单例）。
///
/// 设计要点：
/// 1. **串行队列播放** —— 短时间内多条消息时，提示音排队依次播放，绝不重叠。
///    队列上限 <see cref="MaxQueue"/>，超出直接丢弃（宁可少响几声，也不让内存堆积）。
/// 2. **单后台线程** —— 整个播放循环跑在一条 <see cref="Thread"/> 上（IsBackground = true，
///    不阻止进程退出）。线程按需启动、队列排空后自动退出并释放设备，闲置时零开销、零句柄。
/// 3. **参数在入队时就必须快照**，不能等到播放时才去读全局状态 —— 否则用户中途改路径
///    会把队列里还没播的音效一起改掉。
/// 4. **不缓存解码后的音频数据** —— 每次播放重新读文件。提示音文件有 2MB 上限、通常几十 KB，
///    重复读盘的代价远小于长期驻留一块音频缓冲抢占内存的风险。
/// 5. 所有异常一律吞掉 + 记日志，**绝不允许提示音影响岛体的任何功能**。
/// </summary>
internal static class ToastSoundPlayer
{
    /// <summary>队列上限：超过则丢弃新来的（防止消息轰炸时内存堆积）。</summary>
    private const int MaxQueue = 4;

    /// <summary>单次播放的看门狗超时。提示音极短，超过这个时间还卡着就强制释放设备。</summary>
    private const int PlayWatchdogMs = 8000;

    private static readonly object _lock = new();
    private static readonly Queue<SoundRequest> _queue = new();
    private static Thread? _worker;
    private static int _queued; // 队列中尚未播放完的数量（含正在播放的），供 UI 判定

    /// <summary>一次播放请求：路径 + 音量在入队时就冻结。</summary>
    private readonly record struct SoundRequest(string Path, int VolumePercent);

    /// <summary>当前是否有待播 / 正在播的提示音。</summary>
    internal static bool IsBusy => Volatile.Read(ref _queued) > 0;

    /// <summary>队列中积压的请求数。</summary>
    internal static int PendingCount
    {
        get { lock (_lock) return _queue.Count; }
    }

    /// <summary>
    /// 投递一条提示音。路径为空、文件不存在、队列已满时静默忽略。
    /// 此方法极快（只入队 + 唤醒线程），可以从渲染线程 / HTTP 监听线程任意调用。
    /// </summary>
    internal static void Enqueue(string? path, int volumePercent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return; }

            if (!File.Exists(fullPath)) return;

            lock (_lock)
            {
                if (_queue.Count >= MaxQueue) return; // 队列满，丢弃（不阻塞调用方）
                _queue.Enqueue(new SoundRequest(fullPath, Math.Clamp(volumePercent, 0, 100)));
                Interlocked.Increment(ref _queued);

                if (_worker is null || !_worker.IsAlive)
                {
                    _worker = new Thread(WorkerLoop)
                    {
                        IsBackground = true, // 不阻止进程退出
                        Name = "NPS-ToastSound",
                        Priority = ThreadPriority.BelowNormal, // 绝不和渲染线程抢 CPU
                    };
                    _worker.Start();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[提示音] 入队失败", ex);
        }
    }

    /// <summary>清空队列（例如用户关掉提示音开关时）。正在播的那一声不会被打断。</summary>
    internal static void ClearQueue()
    {
        lock (_lock)
        {
            int dropped = _queue.Count;
            _queue.Clear();
            Interlocked.Add(ref _queued, -dropped);
        }
    }

    /// <summary>播放线程主循环：取一条 → 播完 → 再取下一条；队列空则退出线程。</summary>
    private static void WorkerLoop()
    {
        while (true)
        {
            SoundRequest req;
            lock (_lock)
            {
                if (_queue.Count == 0) { _worker = null; return; }
                req = _queue.Dequeue();
            }

            try
            {
                PlayOnce(req, out string error);
                if (error.Length > 0) Logger.Error($"[提示音] 播放失败：{error}");
            }
            catch (Exception ex)
            {
                Logger.Error("[提示音] 播放异常", ex);
            }
            finally
            {
                Interlocked.Decrement(ref _queued);
            }
        }
    }

    /// <summary>
    /// 真正把一声提示音播完（同步阻塞）。构造顺序：读取器 → 必要时重采样 → WasapiOut。
    /// 无论成功失败，所有 NAudio 对象都在 finally 里释放，绝不泄漏 COM / 文件句柄。
    /// </summary>
    private static void PlayOnce(SoundRequest req, out string error)
    {
        error = "";

        // 🔇 音量为 0% —— 用户把音量拉到最低就是要静音。
        //    这里直接返回，不必初始化 WASAPI、不必解码整个文件，省掉一次设备占用。
        if (req.VolumePercent <= 0) return;

        WaveStream? reader = null;
        IWaveProvider? provider = null;
        WasapiOut? output = null;

        try
        {
            reader = OpenReader(req.Path);
            if (reader is null) { error = "不支持的音频格式或文件已损坏"; return; }

            // 读文件头就知道时长 → 超长文件直接拒播（防止一个几十小时的音频把设备占住）
            double seconds = reader.TotalTime.TotalSeconds;
            if (seconds > ToastSoundConfig.MaxDurationSec)
            {
                error = $"音频时长 {seconds:F1}s 超过上限 {ToastSoundConfig.MaxDurationSec}s";
                return;
            }

            // 🔇 音量必须走「软件增益」，**绝不能碰 `WasapiOut.Volume`**。
            //    ⚠️ NAudio 里 WasapiOut.Volume 的 setter 实现是：
            //        mmDevice.AudioEndpointVolume.MasterVolumeLevelScalar = value;
            //      它改的是**系统主音量**（任务栏音量条），不是本程序的音频会话。
            //      之前这里写过 `output.Volume = req.VolumePercent / 100f;`，
            //      结果每响一声提示音就把系统音量强行改成提示音档位 —— 已移除。
            //    提示音是独立音量通道，增益只在样本上做，与系统音量彻底解耦。
            provider = new VolumeScaleProvider(BuildProvider(reader), req.VolumePercent / 100f);

            output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: false, latency: 120);
            output.Init(provider);

            if (!PlayAndWait(output, (int)Math.Ceiling(seconds * 1000) + 1500))
                error = "播放超时（看门狗强制结束）";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            SafeDispose(output);
            if (provider is IDisposable pd && !ReferenceEquals(provider, reader)) SafeDispose(pd);
            SafeDispose(reader);
        }
    }

    /// <summary>
    /// 播放并等待结束。主路径靠 <see cref="WasapiOut.PlaybackStopped"/> 事件唤醒；
    /// 看门狗只是兜底 —— 设备热插拔 / 被独占时事件可能永远不来，不能死等。
    /// </summary>
    private static bool PlayAndWait(WasapiOut output, int watchdogMs)
    {
        using var stopped = new ManualResetEventSlim(false);
        EventHandler<StoppedEventArgs> handler = (_, _) => { try { stopped.Set(); } catch { } };
        output.PlaybackStopped += handler;
        try
        {
            output.Play();
            return stopped.Wait(Math.Max(500, watchdogMs));
        }
        finally
        {
            output.PlaybackStopped -= handler;
            try { output.Stop(); } catch { }
        }
    }

    /// <summary>按扩展名挑选读取器。只走 NAudio.Core / NAudio.Wasapi 里已有的类型。</summary>
    private static WaveStream? OpenReader(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".wav" => new WaveFileReader(path),
            ".aif" or ".aiff" => new AiffFileReader(path),
            // mp3 / m4a / aac / wma / flac / ogg … 统一交给 Media Foundation（Windows 自带解码器）
            _ => new MediaFoundationReader(path),
        };
    }

    /// <summary>
    /// 把读取器包装成 WasapiOut 能吃的 IWaveProvider。
    /// 多声道（&gt;2）用手写搬运转成立体声，避免引入 NAudio.Wave 包里的扩展方法。
    /// </summary>
    private static IWaveProvider BuildProvider(WaveStream reader)
    {
        int channels = reader.WaveFormat.Channels;
        if (channels > 2)
            return new DownmixToStereoProvider(reader);
        return reader;
    }

    private static void SafeDispose(IDisposable? d)
    {
        try { d?.Dispose(); } catch { }
    }

    /// <summary>
    /// 多声道 → 立体声搬运器（只前向读、不做 seek / 重采样，够提示音用）。
    /// 第一声道给左、第二声道给右，其余声道丢弃。
    /// </summary>
    private sealed class DownmixToStereoProvider : IWaveProvider
    {
        private readonly WaveStream _source;
        private readonly int _srcChannels;
        private readonly int _blockAlign;

        public DownmixToStereoProvider(WaveStream source)
        {
            _source = source;
            _srcChannels = source.WaveFormat.Channels;
            _blockAlign = source.WaveFormat.BlockAlign;
            WaveFormat = new WaveFormat(source.WaveFormat.SampleRate, 16, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_srcChannels <= 2) return _source.Read(buffer, offset, count);

            int outBlock = 4; // 16bit × 2ch
            int frames = count / outBlock;
            int need = frames * _blockAlign;
            byte[] src = new byte[need];
            int got = _source.Read(src, 0, need);
            int gotFrames = got / _blockAlign;

            for (int f = 0; f < gotFrames; f++)
            {
                int s = f * _blockAlign;
                int d = offset + f * outBlock;
                buffer[d] = src[s];
                buffer[d + 1] = src[s + 1];
                buffer[d + 2] = src[s + 2];
                buffer[d + 3] = src[s + 3];
            }
            return gotFrames * outBlock;
        }
    }

    /// <summary>
    /// 软件增益（音量缩放）—— 提示音音量的**唯一**实现方式，与系统音量完全无关。
    ///
    /// ⚠️ 为什么不用 <c>WasapiOut.Volume</c>：
    ///     NAudio 里那个属性的 setter 是
    ///     <c>mmDevice.AudioEndpointVolume.MasterVolumeLevelScalar = value;</c>，
    ///     改的是**系统主音量**（任务栏音量条），而不是本程序的音频会话。
    ///     提示音是独立音量通道，必须与系统音量解耦，所以直接在样本上乘增益。
    ///
    /// 只做原地乘法，不改变格式 / 不改变块对齐，对 <see cref="WasapiOut"/> 完全透明。
    /// 音量 ≥ 100% 时直接透传（零开销）；遇到无法识别的编码也原样透传（宁可响大声，不能变哑巴）。
    /// </summary>
    private sealed class VolumeScaleProvider : IWaveProvider
    {
        private readonly IWaveProvider _source;
        private readonly float _volume;

        public VolumeScaleProvider(IWaveProvider source, float volume)
        {
            _source = source;
            _volume = Math.Clamp(volume, 0f, 1f);
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0 || _volume >= 0.999f) return read;

            var fmt = WaveFormat;
            int bits = fmt.BitsPerSample;
            switch (fmt.Encoding)
            {
                case WaveFormatEncoding.Pcm:
                    if (bits == 8) ScaleU8(buffer, offset, read, _volume);
                    else if (bits == 16) ScaleS16(buffer, offset, read, _volume);
                    else if (bits == 24) ScaleS24(buffer, offset, read, _volume);
                    else if (bits == 32) ScaleS32(buffer, offset, read, _volume);
                    break;
                case WaveFormatEncoding.IeeeFloat:
                    if (bits == 32) ScaleF32(buffer, offset, read, _volume);
                    break;
            }
            return read;
        }

        /// <summary>8bit PCM 是无符号的，中点在 128。</summary>
        private static void ScaleU8(byte[] b, int off, int len, float v)
        {
            for (int i = 0; i < len; i++)
            {
                int s = (int)MathF.Round((b[off + i] - 128) * v);
                b[off + i] = (byte)Math.Clamp(s + 128, 0, 255);
            }
        }

        private static void ScaleS16(byte[] b, int off, int len, float v)
        {
            int n = len / 2 * 2;
            for (int i = 0; i < n; i += 2)
            {
                short s = (short)(b[off + i] | (b[off + i + 1] << 8));
                int q = Math.Clamp((int)MathF.Round(s * v), short.MinValue, short.MaxValue);
                b[off + i] = (byte)(q & 0xFF);
                b[off + i + 1] = (byte)((q >> 8) & 0xFF);
            }
        }

        private static void ScaleS24(byte[] b, int off, int len, float v)
        {
            int n = len / 3 * 3;
            for (int i = 0; i < n; i += 3)
            {
                int s = b[off + i] | (b[off + i + 1] << 8) | (b[off + i + 2] << 16);
                if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000); // 补符号位
                int q = Math.Clamp((int)MathF.Round(s * v), -8388608, 8388607);
                b[off + i] = (byte)(q & 0xFF);
                b[off + i + 1] = (byte)((q >> 8) & 0xFF);
                b[off + i + 2] = (byte)((q >> 16) & 0xFF);
            }
        }

        private static void ScaleS32(byte[] b, int off, int len, float v)
        {
            int n = len / 4 * 4;
            for (int i = 0; i < n; i += 4)
            {
                int s = b[off + i] | (b[off + i + 1] << 8) | (b[off + i + 2] << 16) | (b[off + i + 3] << 24);
                int q = (int)Math.Clamp(s * (double)v, int.MinValue, int.MaxValue);
                b[off + i] = (byte)(q & 0xFF);
                b[off + i + 1] = (byte)((q >> 8) & 0xFF);
                b[off + i + 2] = (byte)((q >> 16) & 0xFF);
                b[off + i + 3] = (byte)((q >> 24) & 0xFF);
            }
        }

        private static void ScaleF32(byte[] b, int off, int len, float v)
        {
            int n = len / 4 * 4;
            for (int i = 0; i < n; i += 4)
            {
                float f = BitConverter.ToSingle(b, off + i) * v;
                BitConverter.TryWriteBytes(new Span<byte>(b, off + i, 4), f);
            }
        }
    }
}
