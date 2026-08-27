using System;
using NAudio.CoreAudioApi;

namespace NotchPeninsula;

/// <summary>
/// 内部音频接口
/// </summary>
internal interface IAudioController : IDisposable
{
    float GetVolume();
    void SetVolume(float level);
    void Mute(bool mute);
}

/// <summary>
/// NAudio 底层适配
/// </summary>
internal sealed class NaudioAdapter : IAudioController
{
    private readonly MMDevice _device;

    public NaudioAdapter()
    {
        _device = new MMDeviceEnumerator()
            .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public float GetVolume()
        => _device.AudioEndpointVolume.MasterVolumeLevelScalar;

    public void SetVolume(float level)
        => _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);

    public void Mute(bool mute)
        => _device.AudioEndpointVolume.Mute = mute;

    public void Dispose() => _device.Dispose();
}

/// <summary>
/// 工厂入口
/// </summary>
internal static class AudioNative
{
    public static IAudioController Create() => new NaudioAdapter();
}