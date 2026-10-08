using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NotchPeninsula;

internal static class ToastSoundPlayer
{
    private const int MaxQueue = 4;

    private const int PlayWatchdogMs = 8000;

    private static readonly object _lock = new();
    private static readonly Queue<SoundRequest> _queue = new();
    private static Thread? _worker;
    private static int _queued; // 队列中尚未播放完的数量（含正在播放的），供 UI 判定

    private readonly record struct SoundRequest(string Path, string ResourceName, int VolumePercent)
    {
        internal bool FromResource => ResourceName.Length > 0;
    }

    internal static bool IsBusy => Volatile.Read(ref _queued) > 0;

    internal static int PendingCount
    {
        get { lock (_lock) return _queue.Count; }
    }

    internal static void Enqueue(string? path, int volumePercent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return; }

            if (!File.Exists(fullPath)) return;

            EnqueueCore(new SoundRequest(fullPath, "", Math.Clamp(volumePercent, 0, 100)));
        }
        catch (Exception ex)
        {
            Logger.Error("[提示音] 入队失败", ex);
        }
    }

    internal static void EnqueueResource(string? resourceName, int volumePercent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(resourceName)) return;
            if (!DataResources.Exists(resourceName)) return;

            EnqueueCore(new SoundRequest("", resourceName, Math.Clamp(volumePercent, 0, 100)));
        }
        catch (Exception ex)
        {
            Logger.Error("[提示音] 入队失败（内嵌资源）", ex);
        }
    }

    private static void EnqueueCore(SoundRequest req)
    {
        lock (_lock)
        {
            if (_queue.Count >= MaxQueue) return; // 队列满，丢弃（不阻塞调用方）
            _queue.Enqueue(req);
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

    internal static void ClearQueue()
    {
        lock (_lock)
        {
            int dropped = _queue.Count;
            _queue.Clear();
            Interlocked.Add(ref _queued, -dropped);
        }
    }

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

    private static void PlayOnce(SoundRequest req, out string error)
    {
        error = "";

        // 音量为 0% —— 用户把音量拉到最低就是要静音。
        if (req.VolumePercent <= 0) return;

        WaveStream? reader = null;
        IWaveProvider? provider = null;
        WasapiOut? output = null;
        Stream? resStream = null; // 内嵌资源流：读取器不拥有它，必须在 finally 里自己释放

        try
        {
            if (req.FromResource)
            {
                resStream = DataResources.OpenRead(req.ResourceName);
                if (resStream is null) { error = "内置提示音资源缺失"; return; }
                reader = ToastSoundConfig.OpenReader(resStream, Path.GetExtension(req.ResourceName));
            }
            else
            {
                reader = OpenReader(req.Path);
                if (reader is null) { error = "不支持的音频格式或文件已损坏"; return; }
            }

            double seconds = reader.TotalTime.TotalSeconds;
            if (seconds > ToastSoundConfig.MaxDurationSec)
            {
                error = $"音频时长 {seconds:F1}s 超过上限 {ToastSoundConfig.MaxDurationSec}s";
                return;
            }

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
            SafeDispose(resStream);
        }
    }

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

    private static WaveStream? OpenReader(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".wav" => new WaveFileReader(path),
            ".aif" or ".aiff" => new AiffFileReader(path),
            _ => new MediaFoundationReader(path),
        };
    }

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
