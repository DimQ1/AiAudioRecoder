using System;
using System.Threading.Tasks;
using NAudio.Wave;

namespace AiAudioRecoder.Services;

public partial class AudioPlaybackService
{
    private WaveOutEvent? _waveOut;
    private AudioFileReader? _audioFile;

    partial void PlatformInitialize()
    {
    }

    private partial async Task<bool> PlatformLoadAsync(string filePath)
    {
        await Task.Yield();
        try
        {
            ResetPlayback();
            _audioFile = new AudioFileReader(filePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioFile);
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            return true;
        }
        catch
        {
            ResetPlayback();
            return false;
        }
    }

    private partial Task PlatformPlayAsync()
    {
        _waveOut?.Play();
        return Task.CompletedTask;
    }

    private partial void PlatformPause()
    {
        _waveOut?.Pause();
    }

    private partial void PlatformStop()
    {
        if (_waveOut is null || _audioFile is null)
            return;

        _waveOut.Stop();
        _audioFile.Position = 0;
    }

    private partial TimeSpan PlatformGetDuration()
    {
        return _audioFile?.TotalTime ?? TimeSpan.Zero;
    }

    private partial TimeSpan PlatformGetPosition()
    {
        return _audioFile?.CurrentTime ?? TimeSpan.Zero;
    }

    private partial bool PlatformIsPlaying()
    {
        return _waveOut?.PlaybackState == PlaybackState.Playing;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _positionTimer.Stop();

        if (_audioFile is not null)
        {
            _audioFile.Position = 0;
        }

        RaisePlaybackEnded();
    }

    private partial void PlatformDispose()
    {
        if (_waveOut is not null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            _waveOut.Dispose();
            _waveOut = null;
        }

        if (_audioFile is not null)
        {
            _audioFile.Dispose();
            _audioFile = null;
        }
    }

    private void ResetPlayback()
    {
        if (_waveOut is not null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            _waveOut.Dispose();
            _waveOut = null;
        }

        if (_audioFile is not null)
        {
            _audioFile.Dispose();
            _audioFile = null;
        }
    }
}
