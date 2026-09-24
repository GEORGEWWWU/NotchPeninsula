using System;

namespace NotchPeninsula;


/// <summary>
/// 内置音量后端（系统主音量）。程序和插件只认它，落到哪儿由 <see cref="VolumeSink"/> 决定：
///
///   连上了 Just Solo LyricServer 的 WS → 操作 **Just Solo 播放器音量**（协议 v1.3.0 的 volume 指令），系统音量一点不动；
///   没有 WS（没在接管 Just Solo）      → 操作 **系统主音量**，Just Solo 那边一点不动。
///
/// 两条通路**互不干扰**：<see cref="RefreshFromSystem"/> 发现的外部系统音量改动只更新系统侧状态，不会去动 Just Solo；
/// Just Solo 侧改的音量也只镜像在 WS 侧（MediaController.TryGetJustSoloVolume），不会反向改系统音量、也不会回发。
/// </summary>
public sealed class SystemSettingsManager : IDisposable
{
    /// <summary>小于它的变化视为没变（吃掉设备侧的浮点 / 取整差异）。</summary>
    private const float MinDelta = 0.0005f;

    private readonly IAudioController? _audio;
    private bool _disposed;

    /// <summary>系统主音量当前值（0.0 ~ 1.0），始终跟着硬件走。</summary>
    public float Volume { get; private set; }

    /// <summary>
    /// 内置音量的下游，由宿主注入：返回 true 表示这次音量已被它接走（连上了 Just Solo 的 WS，音量下发给播放器），
    /// 返回 false 才落回系统主音量。
    /// </summary>
    public Func<float, bool>? VolumeSink { get; set; }

    public SystemSettingsManager()
    {
        try
        {
            _audio = AudioNative.Create();
            Volume = ReadHardware();
            Logger.Info($"SystemSettingsManager 初始化完成，音频控制器已加载，当前系统音量 {Volume:F2}");
        }
        catch (Exception ex)
        {
            _audio = null;
            Logger.Error("SystemSettingsManager 初始化：音频控制器加载失败", ex);
        }
    }

    // ====== 音量 ======

    /// <summary>
    /// 读一次系统音量，被外部改动过（音量键 / 系统 OSD / 其它软件）就更新系统侧状态。
    /// 供轮询调用 —— 只走系统这一条通路，不碰 Just Solo。
    /// </summary>
    public void RefreshFromSystem()
    {
        float level = ReadHardware();
        if (Math.Abs(level - Volume) < MinDelta) return;

        Volume = level;
        Logger.Debug($"系统音量被外部改动：{level:F2}");
    }

    /// <summary>
    /// 设置音量（统一入口）。连上了 Just Solo 的 WS 就操作播放器音量，否则操作系统主音量 —— 二者只走一条。
    /// </summary>
    /// <param name="level">目标音量，0.0 ~ 1.0。</param>
    public void SetSystemVolume(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        Logger.Info($"设置音量：{level:F2}");

        // 被 WS 接走就到此为止：系统音量不变，Just Solo 的音量镜像由客户端自己维护
        if (VolumeSink?.Invoke(level) == true) return;

        WriteHardware(level);
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
            Volume = level;
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
