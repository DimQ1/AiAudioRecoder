using System;
using System.IO;
using System.Threading.Tasks;
#if ANDROID
using Android.Media;
#endif
#if IOS
using AVFoundation;
#endif
using Microsoft.Maui.Storage;
#if WINDOWS
using NAudio.Wave;
#endif

namespace AiAudioRecoder.Services
{
    public interface IAudioRecorderService
    {
        Task<string?> StartRecordingAsync(string? folderName = null, string? fileName = null, string source = "mic");
        Task StopRecordingAsync();
        bool IsRecording { get; }
    }

    public class AudioRecorderService : IAudioRecorderService
    {
        private bool _isRecording;
        private string? _filePath;
#if WINDOWS
        private WaveInEvent? _waveIn;
        private WaveFileWriter? _writer;
#endif

        public bool IsRecording => _isRecording;

        public Task<string?> StartRecordingAsync(string? folderName = null, string? fileName = null, string source = "mic")
        {
            if (_isRecording) return Task.FromResult<string?>(null);
            var dateFolder = folderName ?? DateTime.Now.ToString("yyyy-MM-dd");
            var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dir = Path.Combine(root, "AiAudioRecoder", dateFolder);
            Directory.CreateDirectory(dir);
            var name = fileName ?? $"audio_{DateTime.Now:yyyyMMdd_HHmmss}";
            if (source == "system")
                name += "_system";
            name += ".wav";
            _filePath = Path.Combine(dir, name);

            if (source == "system")
            {
                // Stub for system audio: create empty file
                File.WriteAllBytes(_filePath, new byte[0]);
                return Task.FromResult<string?>(_filePath);
            }

#if ANDROID
            // Android: Use MediaRecorder for mic. System audio requires special permissions/root.
            // TODO: Implement MediaRecorder
#elif IOS
            // iOS: Use AVAudioRecorder for mic. System audio capture is not allowed by Apple.
            // TODO: Implement AVAudioRecorder
#elif WINDOWS
            try
            {
                _waveIn = new WaveInEvent();
                _waveIn.WaveFormat = new WaveFormat(44100, 1); // 44.1kHz, mono
                _waveIn.DataAvailable += OnDataAvailable;
                _writer = new WaveFileWriter(_filePath, _waveIn.WaveFormat);
                _waveIn.StartRecording();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error starting recording ({source}): {ex.Message}");
                return Task.FromResult<string?>(null);
            }
#endif
            _isRecording = true;
            return Task.FromResult<string?>(_filePath);
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }

        public Task StopRecordingAsync()
        {
            if (!_isRecording) return Task.CompletedTask;
#if ANDROID
            // TODO: Stop MediaRecorder
#elif IOS
            // TODO: Stop AVAudioRecorder
#elif WINDOWS
            try
            {
                _waveIn?.StopRecording();
                _writer?.Dispose();
                _waveIn?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error stopping recording: {ex.Message}");
            }
#endif
            _isRecording = false;
            return Task.CompletedTask;
        }
    }
}
