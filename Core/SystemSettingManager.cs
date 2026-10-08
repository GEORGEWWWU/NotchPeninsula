
namespace NotchPeninsula;

public enum BrightnessCapability
{
    NotSupported,
    SoftwareOnly,
    Hardware
}
public sealed class SystemSettingsManager : IDisposable
{
    private const float MinDelta = 0.0005f;

    private readonly IAudioController? _audio;
    private bool _disposed;

    public float Volume { get; private set; }

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

    // ---- 音量 ----

    public void RefreshFromSystem()
    {
        float level = ReadHardware();
        if (Math.Abs(level - Volume) < MinDelta) return;

        Volume = level;
        Logger.Debug($"系统音量被外部改动：{level:F2}");
    }

    public void SetSystemVolume(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        Logger.Info($"设置音量：{level:F2}");

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
// ---- 亮度 ----
    public BrightnessCapability BrightnessCapability
    {
        get
        {
            var cap = BrightnessManager.Capability;
            Logger.Debug($"读取亮度能力：{cap}");
            return cap;
        }
    }

    public int GetScreenBrightness()
    {
        var b = BrightnessManager.Get();
        Logger.Debug($"读取硬件亮度：{b}");
        return b;
    }

    public bool TrySetScreenBrightness(int percent)
    {
        Logger.Info($"设置硬件亮度：{percent}%");
        var ok = BrightnessManager.TrySet(percent);
        if (!ok) Logger.Warn($"设置硬件亮度失败（不支持？）：{percent}%");
        return ok;
    }

    public int GetSimulatedBrightness()
    {
        var b = BrightnessManager.GetSimulated();
        Logger.Debug($"读取模拟亮度：{b}");
        return b;
    }

    public void SetSimulatedBrightness(int percent)
    {
        Logger.Info($"设置模拟亮度：{percent}%");
        try
        {
            BrightnessManager.SetSimulated(percent);
        }
        catch (Exception ex)
        {
            Logger.Error($"设置模拟亮度失败，percent={percent}", ex);
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
