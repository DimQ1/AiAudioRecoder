using System;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Maui.ApplicationModel;

namespace AiAudioRecoder.Services;

public partial class AudioPlaybackService : IDisposable
{
    private readonly System.Timers.Timer _positionTimer;
    private bool _isLoaded;

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? PlaybackEnded;

    public AudioPlaybackService()
    {
        _positionTimer = new System.Timers.Timer(200);
        _positionTimer.Elapsed += OnTimerElapsed;
        _positionTimer.AutoReset = true;
        PlatformInitialize();
    }

    public async Task<bool> LoadAsync(string filePath)
    {
        _positionTimer.Stop();
        var loaded = await PlatformLoadAsync(filePath);
        _isLoaded = loaded;
        if (loaded)
        {
            RaisePositionChanged();
        }
        return loaded;
    }

    public async Task<bool> PlayAsync()
    {
        if (!_isLoaded)
            return false;

        await PlatformPlayAsync();
        _positionTimer.Start();
        return true;
    }

    public void Pause()
    {
        if (!_isLoaded)
            return;

        PlatformPause();
        _positionTimer.Stop();
    }

    public void Stop()
    {
        if (!_isLoaded)
            return;

        PlatformStop();
        _positionTimer.Stop();
        RaisePositionChanged();
    }

    public TimeSpan Duration => _isLoaded ? PlatformGetDuration() : TimeSpan.Zero;
    public TimeSpan Position => _isLoaded ? PlatformGetPosition() : TimeSpan.Zero;
    public bool IsPlaying => _isLoaded && PlatformIsPlaying();

    private void RaisePositionChanged()
    {
        if (!_isLoaded)
            return;

        var position = PlatformGetPosition();
        MainThread.BeginInvokeOnMainThread(() => PositionChanged?.Invoke(this, position));
    }

    protected void RaisePlaybackEnded()
    {
        MainThread.BeginInvokeOnMainThread(() => PlaybackEnded?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        _positionTimer.Elapsed -= OnTimerElapsed;
        _positionTimer.Dispose();
        PlatformDispose();
    }

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e) => RaisePositionChanged();

    partial void PlatformInitialize();
    private partial Task<bool> PlatformLoadAsync(string filePath);
    private partial Task PlatformPlayAsync();
    private partial void PlatformPause();
    private partial void PlatformStop();
    private partial TimeSpan PlatformGetDuration();
    private partial TimeSpan PlatformGetPosition();
    private partial bool PlatformIsPlaying();
    private partial void PlatformDispose();
}
