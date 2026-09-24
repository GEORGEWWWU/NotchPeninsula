using System;

namespace NotchPeninsula;


/// <summary>
/// 内置音量后端（唯一真源）。程序和插件只认它，具体落到哪儿由 <see cref="VolumeSink"/> 决定：
///
///   有 Just Solo LyricServer 的 WS 连接 → 下发给 Just Solo 播放器（协议 v1.3.0 的 volume 指令），**不动系统音量**；
///   没有 WS（没在接管 Just Solo）      → 改系统主音量，**二者只走一条**，不会同时改。
///
/// 三条来源都汇到这里：
///   ① 用户按音量键 / 系统 OSD / 其它软件改系统音量 —— <see cref="RefreshFromSystem"/> 轮询发现（系统中性改动照旧）；
///   ② 本程序（含插件）主动设置 —— <see cref="SetSystemVolume"/>；
///   ③ Just Solo 回推音量 —— <c>SetSystemVolume(level, notify: false)</c>，只落状态。
/// </summary>
public sealed class SystemSettingsManager : IDisposable
{
    /// <summary>小于它的变化视为没变（也顺带吃掉 Just Solo 侧的浮点 / 取整漂移）。</summary>
    private const float MinDelta = 0.0005f;

    private readonly IAudioController? _audio;
    private bool _disposed;

    /// <summary>最近一次读写到的系统主音量，用来发现「不经过本类」的外部改动。</summary>
    private float _hardwareVolume;

    /// <summary>内置音量当前值（0.0 ~ 1.0）。</summary>
    public float Volume { get; private set; }

    /// <summary>
    /// 内置音量的优先下游，由宿主注入：返回 true 表示这次音量已被它接走（有 Just Solo 的 WS，音量下发给播放器），
    /// 返回 false 才落回系统主音量。
    /// </summary>
    public Func<float, bool>? VolumeSink { get; set; }

    public SystemSettingsManager()
    {
        try
        {
            _audio = AudioNative.Create();
            _hardwareVolume = ReadHardware();
            Volume = _hardwareVolume;
            Logger.Info($"SystemSettingsManager 初始化完成，音频控制器已加载，当前音量 {Volume:F2}");
        }
        catch (Exception ex)
        {
            _audio = null;
            Logger.Error("SystemSettingsManager 初始化：音频控制器加载失败", ex);
        }
    }

    // ====== 音量 ======

    /// <summary>
    /// 读一次系统音量，发现外部改动（音量键 / 系统 OSD / 其它软件）就同步进内置音量。
    /// 供轮询调用；外部改动同样会走一遍下游（接了 Just Solo 就同步给播放器）。
    /// </summary>
    public void RefreshFromSystem()
    {
        float level = ReadHardware();
        if (Math.Abs(level - _hardwareVolume) < MinDelta) return;

        _hardwareVolume = level;
        if (Math.Abs(level - Volume) < MinDelta) return;

        Volume = level;
        VolumeSink?.Invoke(level);
    }

    /// <summary>设置内置音量（统一入口）。</summary>
    /// <param name="level">目标音量，0.0 ~ 1.0。</param>
    /// <param name="notify">
    /// true（默认）= 真实变化，按 <see cref="VolumeSink"/> 落到 Just Solo（有 WS）或系统主音量。
    /// false = 这个值本身来自 Just Solo 回推：只落状态，不再下发、也不改系统音量 ——
    ///         协议明确要求收到服务端 volume 后不得回发，否则来回抖动。
    /// </param>
    public void SetSystemVolume(float level, bool notify = true)
    {
        level = Math.Clamp(level, 0f, 1f);

        if (!notify)
        {
            Volume = level;
            return;
        }

        Logger.Info($"设置内置音量：{level:F2}");
        // 有 Just Solo 的 WS 就改播放器音量，没人接才改系统主音量
        if (VolumeSink?.Invoke(level) != true) WriteHardware(level);

        if (Math.Abs(level - Volume) < MinDelta) return;
        Volume = level;
    }

    public void Mute(bool mute)
    {
        Logger.Info($"设置静音：{mute}");
        try
        {
            _audio?.Mute(mute);
        }
        catch (Exception ex)
        {
            Logger.Error($"设置静音失败，mute={mute}", ex);
        }
    }

    /// <summary>读系统音量，失败按 0 处理（与设备被拔掉时的表现一致）。</summary>
    private float ReadHardware()
    {
        try
        {
            return _audio?.GetVolume() ?? 0f;
        }
        catch (Exception ex)
        {
            Logger.Error("读取系统音量失败", ex);
            return 0f;
        }
    }

    private void WriteHardware(float level)
    {
        try
        {
            _audio?.SetVolume(level);
            _hardwareVolume = level;
        }
        catch (Exception ex)
        {
            Logger.Error($"设置系统音量失败，level={level}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _audio?.Dispose();
            Logger.Info("SystemSettingsManager 已释放资源");
        }
        catch (Exception ex)
        {
            Logger.Error("SystemSettingsManager 释放资源异常", ex);
        }
    }
}
