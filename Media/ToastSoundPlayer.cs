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

            provider = BuildProvider(reader);

            output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: false, latency: 120);
            output.Volume = req.VolumePercent / 100f;
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
}
