using System;

namespace NotchPeninsula;


public sealed class SystemSettingsManager : IDisposable
{
    private readonly IAudioController? _audio;
    private bool _disposed;

    public SystemSettingsManager()
    {
        try
        {
            _audio = AudioNative.Create();
            Logger.Info("SystemSettingsManager 初始化完成，音频控制器已加载");
        }
        catch (Exception ex)
        {
            _audio = null;
            Logger.Error("SystemSettingsManager 初始化：音频控制器加载失败", ex);
        }
    }

    // ====== 音量 ======
    public float GetSystemVolume()
    {
        var v = _audio?.GetVolume() ?? 0f;
        Logger.Debug($"读取系统音量：{v:F2}");
        return v;
    }

    /// <summary>level: 0.0 ~ 1.0</summary>
    public void SetSystemVolume(float level)
    {
        Logger.Info($"设置系统音量：{level:F2}");
        try
        {
            _audio?.SetVolume(level);
        }
        catch (Exception ex)
        {
            Logger.Error($"设置系统音量失败，level={level}", ex);
        }
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
