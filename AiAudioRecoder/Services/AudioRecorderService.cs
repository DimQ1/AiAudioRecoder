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
using NAudio.Wave.SampleProviders; // added for mixing
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
        
        // Mixed recording (new implementation)
        private WaveInEvent? _mixedMic;
        private WasapiLoopbackCapture? _mixedSystem;
        private WaveFileWriter? _mixedWriter;
        private MixingSampleProvider? _mixer;
        private ISampleProvider? _micSampleProvider;
        private ISampleProvider? _systemSampleProvider;
        private CancellationTokenSource? _mixCts;
        private Task? _mixTask;
        private MediaFoundationResampler? _systemResampler; // if resampling needed
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
                    _loopback.DataAvailable += (object? sender, WaveInEventArgs e) =>
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
                File.WriteAllBytes(_filePath, new byte[0]);
#endif
            }
            else // mic
            {
#if WINDOWS
                try
                {
                    _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(44100, 1) }; // mono mic
                    _waveIn.DataAvailable += (object? sender, WaveInEventArgs e) =>
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
            // Mixed recording stop
            if (_mixCts != null)
            {
                try { _mixCts.Cancel(); } catch { }
                try { _mixTask?.Wait(3000); } catch { }
            }

            if (_mixedMic != null)
            {
                try { _mixedMic.StopRecording(); } catch { }
                _mixedMic.Dispose();
                _mixedMic = null;
            }
            if (_mixedSystem != null)
            {
                try { _mixedSystem.StopRecording(); } catch { }
                _mixedSystem.Dispose();
                _mixedSystem = null;
            }
            _systemResampler?.Dispose();
            _systemResampler = null;
            _mixedWriter?.Dispose();
            _mixedWriter = null;
            _mixer = null;
            _micSampleProvider = null;
            _systemSampleProvider = null;
            _mixCts?.Dispose();
            _mixCts = null;

            // Single source stop (if used)
            if (_loopback != null)
            {
                try { _loopback.StopRecording(); } catch { }
                _writer?.Dispose();
                _loopback.Dispose();
                _loopback = null;
            }
            if (_waveIn != null)
            {
                try { _waveIn.StopRecording(); } catch { }
                _writer?.Dispose();
                _waveIn.Dispose();
                _waveIn = null;
            }
            _writer = null;
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
                // Mixer works in FLOAT 32 -> we will convert to 16-bit PCM for broader compatibility
                var mixerFloatFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
                var outputPcmFormat = new WaveFormat(44100, 16, 2);

                // Mic capture (mono 16-bit -> float -> stereo)
                _mixedMic = new WaveInEvent { WaveFormat = new WaveFormat(44100, 1) };
                var micBuffered = new BufferedWaveProvider(_mixedMic.WaveFormat) { DiscardOnBufferOverflow = true };
                _mixedMic.DataAvailable += (s, e) => micBuffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
                ISampleProvider micSample = micBuffered.ToSampleProvider(); // 16-bit -> float
                micSample = new MonoToStereoSampleProvider(micSample);
                _micSampleProvider = micSample;

                // System capture (device format -> resample if needed -> stereo float)
                _mixedSystem = new WasapiLoopbackCapture();
                var sysFormat = _mixedSystem.WaveFormat;
                var sysBuffered = new BufferedWaveProvider(sysFormat) { DiscardOnBufferOverflow = true };
                _mixedSystem.DataAvailable += (s, e) => sysBuffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
                ISampleProvider sysSample = sysBuffered.ToSampleProvider();
                if (sysFormat.SampleRate != 44100 || sysFormat.Channels != 2 || sysFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                {
                    // Resample to 44.1k stereo float
                    var resampleTarget = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
                    _systemResampler = new MediaFoundationResampler(sysBuffered, resampleTarget) { ResamplerQuality = 60 };
                    sysSample = _systemResampler.ToSampleProvider();
                }
                _systemSampleProvider = sysSample;

                // Build mixer
                _mixer = new MixingSampleProvider(mixerFloatFormat) { ReadFully = true };
                _mixer.AddMixerInput(_micSampleProvider);
                _mixer.AddMixerInput(_systemSampleProvider);

                // Prepare writer with PCM 16-bit format
                _mixedWriter = new WaveFileWriter(mixedPath, outputPcmFormat);
                _mixCts = new CancellationTokenSource();

                _mixedMic.StartRecording();
                _mixedSystem.StartRecording();

                _mixTask = Task.Run(async () =>
                {
                    // 100ms buffer (float samples)
                    var floatBuffer = new float[mixerFloatFormat.SampleRate / 10 * mixerFloatFormat.Channels];
                    var pcmBuffer = new byte[floatBuffer.Length * 2]; // 16-bit per sample
                    try
                    {
                        while (!_mixCts!.IsCancellationRequested)
                        {
                            int read = _mixer!.Read(floatBuffer, 0, floatBuffer.Length);
                            if (read > 0)
                            {
                                // Convert float [-1,1] -> 16-bit PCM little-endian
                                int byteIndex = 0;
                                for (int i = 0; i < read; i++)
                                {
                                    var sample = floatBuffer[i];
                                    if (sample > 1f) sample = 1f; else if (sample < -1f) sample = -1f;
                                    short pcm = (short)(sample * short.MaxValue);
                                    pcmBuffer[byteIndex++] = (byte)(pcm & 0xFF);
                                    pcmBuffer[byteIndex++] = (byte)((pcm >> 8) & 0xFF);
                                }
                                _mixedWriter!.Write(pcmBuffer, 0, byteIndex);
                            }
                            else
                            {
                                await Task.Delay(10, _mixCts.Token);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Mixer loop error: {ex.Message}");
                    }
                }, _mixCts.Token);

                _isRecording = true;
                _filePath = mixedPath;
                System.Diagnostics.Debug.WriteLine($"[AudioRecorder] Mixed recording started: {mixedPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error starting mixed recording: {ex.Message}");
                StopRecordingAsync();
                return Task.FromResult<string?>(null);
            }
#else
            File.WriteAllBytes(mixedPath, new byte[0]);
            _isRecording = true;
            _filePath = mixedPath;
#endif
            return Task.FromResult<string?>(mixedPath);
        }

#if WINDOWS
        // (Removed old queue-based MixAudioAsync and CleanupMixedRecording - now handled in StopRecordingAsync)
#endif
    }
}
