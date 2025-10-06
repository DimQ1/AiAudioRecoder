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
using NAudio.Wasapi;
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
        private WasapiLoopbackCapture? _loopback;
        private WaveFileWriter? _writer;
#endif

        public bool IsRecording => _isRecording;

        public Task<string?> StartRecordingAsync(string? folderName = null, string? fileName = null, string source = "mic")
        {
            if (_isRecording) return Task.FromResult<string?>(null);
            var dateFolder = string.IsNullOrEmpty(folderName) ? DateTime.Now.ToString("yyyy-MM-dd") : folderName;
            var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dir = Path.Combine(root, "AiAudioRecoder", "audioData", dateFolder);
            Directory.CreateDirectory(dir);
            var name = string.IsNullOrEmpty(fileName) ? $"audio_{DateTime.Now:yyyyMMdd_HHmmss}" : fileName;
            if (source == "system")
                name += "_system";
            name += ".wav";
            _filePath = Path.Combine(dir, name);

            if (source == "system")
            {
#if WINDOWS
                try
                {
                    _loopback = new WasapiLoopbackCapture();
                    _loopback.DataAvailable += OnDataAvailable;
                    _writer = new WaveFileWriter(_filePath, _loopback.WaveFormat);
                    _loopback.StartRecording();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error starting loopback recording: {ex.Message}");
                    return Task.FromResult<string?>(null);
                }
#else
                // Stub for non-Windows: empty file
                File.WriteAllBytes(_filePath, new byte[0]);
#endif
            }
            else // mic
            {
#if WINDOWS
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
                    System.Diagnostics.Debug.WriteLine($"Error starting mic recording: {ex.Message}");
                    return Task.FromResult<string?>(null);
                }
#elif ANDROID
                // TODO: MediaRecorder for mic
#elif IOS
                // TODO: AVAudioRecorder for mic
#endif
            }

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
#if WINDOWS
            if (_loopback != null)
            {
                try
                {
                    _loopback.StopRecording();
                    _writer?.Dispose();
                    _loopback.Dispose();
                    _loopback = null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error stopping loopback: {ex.Message}");
                }
            }
            else if (_waveIn != null)
            {
                try
                {
                    _waveIn.StopRecording();
                    _writer?.Dispose();
                    _waveIn.Dispose();
                    _waveIn = null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error stopping mic: {ex.Message}");
                }
            }
#elif ANDROID
            // TODO: Stop MediaRecorder
#elif IOS
            // TODO: Stop AVAudioRecorder
#endif
            _isRecording = false;
            return Task.CompletedTask;
        }
    }
}
