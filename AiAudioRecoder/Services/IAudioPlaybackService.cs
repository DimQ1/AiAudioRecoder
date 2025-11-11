namespace AiAudioRecoder.Services
{
    public interface IAudioPlaybackService
    {
        event EventHandler<TimeSpan>? PositionChanged;
        event EventHandler? PlaybackEnded;
        event EventHandler? PlaybackStopped;

        Task<bool> LoadAsync(string filePath);
        Task PlayAsync();
        void Pause();
        void Stop();

        TimeSpan Duration { get; }
        TimeSpan Position { get; }
        bool IsPlaying { get; }
        bool IsLoaded { get; }
        TimeSpan TotalDuration { get; }
        TimeSpan CurrentPosition { get; set; }
    }
}
