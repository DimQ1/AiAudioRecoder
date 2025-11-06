using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;
#if ANDROID
using Android.Media;
#endif
#if IOS
using AVFoundation;
#endif
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
        Task<string?> StartMixedRecordingAsync(string? folderName = null, string? fileName = null);
    }

    public class AudioRecorderService : IAudioRecorderService
    {
        private bool _isRecording;
        private string? _filePath;
#if WINDOWS
        private WaveInEvent? _waveIn;
        private WasapiLoopbackCapture? _loopback;
        private WaveFileWriter? _writer;
        
        // For mixed recording
        private WaveInEvent? _mixedWaveIn;
        private WasapiLoopbackCapture? _mixedLoopback;
        private WaveFileWriter? _mixedWriter;
        private readonly ConcurrentQueue<byte[]> _micBuffer = new();
        private readonly ConcurrentQueue<byte[]> _systemBuffer = new();
        private bool _mixingActive;
        private Task? _mixingTask;
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
                    _loopback.DataAvailable += (object? sender, NAudio.Wave.WaveInEventArgs e) =>
                    {
                        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                    };
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
                    _waveIn.DataAvailable += (object? sender, NAudio.Wave.WaveInEventArgs e) =>
                    {
                        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                    };
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

        public Task StopRecordingAsync()
        {
            if (!_isRecording) return Task.CompletedTask;
#if WINDOWS
            // Stop mixing if active
            if (_mixingActive)
            {
                _mixingActive = false;
                if (_mixingTask != null)
                {
                    try
                    {
                        _mixingTask.Wait(5000);
                    }
                    catch { }
                }
            }

            if (_mixedWaveIn != null || _mixedLoopback != null)
            {
                CleanupMixedRecording();
            }
            else if (_loopback != null)
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

        public Task<string?> StartMixedRecordingAsync(string? folderName = null, string? fileName = null)
        {
            if (_isRecording) return Task.FromResult<string?>(null);

            var dateFolder = string.IsNullOrEmpty(folderName) ? DateTime.Now.ToString("yyyy-MM-dd") : folderName;
            var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dir = Path.Combine(root, "AiAudioRecoder", "audioData", dateFolder);
            Directory.CreateDirectory(dir);
            var baseName = string.IsNullOrEmpty(fileName) ? $"audio_{DateTime.Now:yyyyMMdd_HHmmss}" : fileName;
            var mixedPath = Path.Combine(dir, $"{baseName}.wav");

#if WINDOWS
            try
            {
                // Запускаем запись микрофона
                _mixedWaveIn = new WaveInEvent();
                _mixedWaveIn.WaveFormat = new WaveFormat(44100, 1); // Mono input
                _mixedWaveIn.DataAvailable += (object? sender, NAudio.Wave.WaveInEventArgs e) =>
                {
                    // Копируем данные микрофона в буфер
                    byte[] buffer = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
                    _micBuffer.Enqueue(buffer);
                };
                _mixedWaveIn.StartRecording();

                // Запускаем запись системного аудио
                _mixedLoopback = new WasapiLoopbackCapture();
                _mixedLoopback.DataAvailable += (object? sender, NAudio.Wave.WaveInEventArgs e) =>
                {
                    // Копируем данные системного аудио в буфер
                    byte[] buffer = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
                    _systemBuffer.Enqueue(buffer);
                };
                _mixedLoopback.StartRecording();

                // Создаём Writer для смешанного аудио - используем формат микшера
                var mixerFormat = new WaveFormat(44100, 16, 2); // 44.1kHz, 16-bit, Stereo
                _mixedWriter = new WaveFileWriter(mixedPath, mixerFormat);

                // Запускаем поток для микширования
                _mixingActive = true;
                _mixingTask = MixAudioAsync();

                _isRecording = true;
                _filePath = mixedPath;

                System.Diagnostics.Debug.WriteLine($"Started mixed recording to {mixedPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error starting mixed recording: {ex.Message}");
                CleanupMixedRecording();
                return Task.FromResult<string?>(null);
            }
#else
            // Stub для не-Windows платформ
            File.WriteAllBytes(mixedPath, new byte[0]);
#endif

            return Task.FromResult(mixedPath);
        }

        private async Task MixAudioAsync()
        {
#if WINDOWS
            try
            {
                while (_mixingActive && _isRecording)
                {
                    bool hasMic = _micBuffer.TryDequeue(out byte[]? micData);
                    bool hasSystem = _systemBuffer.TryDequeue(out byte[]? systemData);

                    if (hasMic || hasSystem)
                    {
                        // Микшируем два источника в стерео
                        byte[] micSamples = micData ?? Array.Empty<byte>();
                        byte[] systemSamples = systemData ?? Array.Empty<byte>();

                        int maxLength = Math.Max(micSamples.Length, systemSamples.Length);
                        byte[] stereoBuffer = new byte[maxLength];

                        // Микрофон -> левый канал, Система -> правый канал
                        for (int i = 0; i < maxLength; i += 2)
                        {
                            // Левый канал (микрофон)
                            if (i < micSamples.Length)
                            {
                                stereoBuffer[i] = micSamples[i];
                                if (i + 1 < micSamples.Length)
                                    stereoBuffer[i + 1] = micSamples[i + 1];
                            }

                            // Правый канал (система)
                            if (i < systemSamples.Length)
                            {
                                if (i + 2 < stereoBuffer.Length)
                                    stereoBuffer[i + 2] = systemSamples[i];
                                if (i + 3 < stereoBuffer.Length && i + 1 < systemSamples.Length)
                                    stereoBuffer[i + 3] = systemSamples[i + 1];
                            }
                        }

                        _mixedWriter?.Write(stereoBuffer, 0, stereoBuffer.Length);
                    }
                    else
                    {
                        await Task.Delay(10);
                    }
                }

                // Записываем оставшиеся данные
                while (_micBuffer.TryDequeue(out byte[]? remaining))
                {
                    _mixedWriter?.Write(remaining, 0, remaining.Length);
                }
                while (_systemBuffer.TryDequeue(out byte[]? remaining))
                {
                    _mixedWriter?.Write(remaining, 0, remaining.Length);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in mixed recording: {ex.Message}");
            }
#endif
        }

        private void CleanupMixedRecording()
        {
#if WINDOWS
            if (_mixedWaveIn != null)
            {
                try
                {
                    _mixedWaveIn.StopRecording();
                    _mixedWaveIn.Dispose();
                    _mixedWaveIn = null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error cleaning up mic: {ex.Message}");
                }
            }

            if (_mixedLoopback != null)
            {
                try
                {
                    _mixedLoopback.StopRecording();
                    _mixedLoopback.Dispose();
                    _mixedLoopback = null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error cleaning up loopback: {ex.Message}");
                }
            }

            _mixedWriter?.Dispose();
            _mixedWriter = null;
            
            _micBuffer.Clear();
            _systemBuffer.Clear();
#endif
        }
    }
}
