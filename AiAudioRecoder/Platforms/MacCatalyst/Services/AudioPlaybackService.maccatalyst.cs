using System;
using System.Threading.Tasks;

namespace AiAudioRecoder.Services;

public partial class AudioPlaybackService
{
    partial void PlatformInitialize()
    {
    }

    private partial Task<bool> PlatformLoadAsync(string filePath)
    {
        return Task.FromResult(false);
    }

    private partial Task PlatformPlayAsync()
    {
        return Task.CompletedTask;
    }

    private partial void PlatformPause()
    {
    }

    private partial void PlatformStop()
    {
    }

    private partial TimeSpan PlatformGetDuration()
    {
        return TimeSpan.Zero;
    }

    private partial TimeSpan PlatformGetPosition()
    {
        return TimeSpan.Zero;
    }

    private partial bool PlatformIsPlaying()
    {
        return false;
    }

    private partial void PlatformDispose()
    {
    }
}
