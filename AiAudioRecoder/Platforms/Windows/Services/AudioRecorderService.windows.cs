#if WINDOWS
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wasapi;
using NAudio.Wave.SampleProviders;

namespace AiAudioRecoder.Services;

public partial class AudioRecorderService
{
    // Mixed recording
    private WaveInEvent? _mixedMic;
    private WasapiLoopbackCapture? _mixedSystem;
    private WaveFileWriter? _mixedWriter;
    private MixingSampleProvider? _mixer;
    private ISampleProvider? _micSampleProvider;
    private ISampleProvider? _systemSampleProvider;
    private CancellationTokenSource? _mixCts;
    private Task? _mixTask;
    private MediaFoundationResampler? _systemResampler;

    private class MutingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly Func<bool> _enabledFunc;
        public MutingSampleProvider(ISampleProvider source, Func<bool> enabledFunc)
        {
            _source = source;
            _enabledFunc = enabledFunc;
        }
        public WaveFormat WaveFormat => _source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_enabledFunc())
            {
                Array.Clear(buffer, offset, read); // mute samples
            }
            return read;
        }
    }

    // Always route through mixed API for consistency
    public Task<string?> StartRecordingAsync(string? folderName = null, string? fileName = null, string source = "mic")
        => StartMixedRecordingAsync(folderName, fileName);

    public Task<string?> StartMixedRecordingAsync(string? folderName = null, string? fileName = null)
    {
        if (_isRecording) return Task.FromResult<string?>(null);
        var dateFolder = string.IsNullOrEmpty(folderName) ? DateTime.Now.ToString("yyyy-MM-dd") : folderName;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dir = Path.Combine(root, "AiAudioRecoder", "audioData", dateFolder);
        Directory.CreateDirectory(dir);
        var baseName = string.IsNullOrEmpty(fileName) ? $"audio_{DateTime.Now:yyyyMMdd_HHmmss}" : fileName;
        var mixedPath = Path.Combine(dir, $"{baseName}.wav");
        try
        {
            var mixerFloat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var outputFormat = new WaveFormat(44100, 16, 2);

            // Mic capture device always started to allow enabling later
            _mixedMic = new WaveInEvent { WaveFormat = new WaveFormat(44100, 1) };
            var micBuffer = new BufferedWaveProvider(_mixedMic.WaveFormat) { DiscardOnBufferOverflow = true };
            _mixedMic.DataAvailable += (s, e) => micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            var micStereo = new MonoToStereoSampleProvider(micBuffer.ToSampleProvider());
            _micSampleProvider = new MutingSampleProvider(micStereo, () => _micEnabled);

            // System capture device always started to allow enabling later
            _mixedSystem = new WasapiLoopbackCapture();
            var sysFormat = _mixedSystem.WaveFormat;
            var sysBuffer = new BufferedWaveProvider(sysFormat) { DiscardOnBufferOverflow = true };
            _mixedSystem.DataAvailable += (s, e) => sysBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            ISampleProvider sysSample = sysBuffer.ToSampleProvider();
            if (sysFormat.SampleRate != 44100 || sysFormat.Channels != 2 || sysFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            {
                _systemResampler = new MediaFoundationResampler(sysBuffer, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ResamplerQuality = 60 }; // 44.1k stereo float
                sysSample = _systemResampler.ToSampleProvider();
            }
            _systemSampleProvider = new MutingSampleProvider(sysSample, () => _systemEnabled);

            _mixer = new MixingSampleProvider(mixerFloat) { ReadFully = false }; // do not auto fill silence (prevents faster-than-real-time)
            _mixer.AddMixerInput(_micSampleProvider);
            _mixer.AddMixerInput(_systemSampleProvider);

            _mixedWriter = new WaveFileWriter(mixedPath, outputFormat);
            _mixCts = new CancellationTokenSource();
            _mixedMic.StartRecording();
            _mixedSystem.StartRecording();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _mixTask = Task.Run(async () =>
            {
                int framesPerChunk = mixerFloat.SampleRate / 10; // 100ms target
                int samplesPerChunk = framesPerChunk * mixerFloat.Channels;
                var floatBuffer = new float[samplesPerChunk];
                var pcmBuffer = new byte[samplesPerChunk * 2];
                long totalFramesWritten = 0;
                try
                {
                    while (!_mixCts!.IsCancellationRequested)
                    {
                        int readSamples = _mixer!.Read(floatBuffer, 0, samplesPerChunk);
                        if (readSamples > 0)
                        {
                            int framesRead = readSamples / mixerFloat.Channels;
                            int idx = 0;
                            for (int i = 0; i < readSamples; i++)
                            {
                                var sample = floatBuffer[i];
                                if (sample > 1f) sample = 1f; else if (sample < -1f) sample = -1f;
                                short pcm = (short)(sample * short.MaxValue);
                                pcmBuffer[idx++] = (byte)(pcm & 0xFF);
                                pcmBuffer[idx++] = (byte)((pcm >> 8) & 0xFF);
                            }
                            _mixedWriter!.Write(pcmBuffer, 0, idx);
                            totalFramesWritten += framesRead;

                            // Pace to real-time: expected elapsed for written frames
                            double expectedMs = totalFramesWritten * 1000.0 / mixerFloat.SampleRate;
                            double actualMs = sw.Elapsed.TotalMilliseconds;
                            int delayMs = (int)(expectedMs - actualMs);
                            if (delayMs > 0)
                            {
                                try { await Task.Delay(delayMs, _mixCts.Token); } catch { }
                            }
                        }
                        else
                        {
                            // No data yet - short wait
                            try { await Task.Delay(5, _mixCts.Token); } catch { }
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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Mixed start error: {ex.Message}");
            StopRecordingAsync();
            return Task.FromResult<string?>(null);
        }
        return Task.FromResult<string?>(mixedPath);
    }

    public Task StopRecordingAsync()
    {
        if (!_isRecording) return Task.CompletedTask;
        if (_mixCts != null)
        {
            try { _mixCts.Cancel(); } catch { }
            try { _mixTask?.Wait(1500); } catch { }
        }
        _mixedMic?.StopRecording(); _mixedMic?.Dispose(); _mixedMic = null;
        _mixedSystem?.StopRecording(); _mixedSystem?.Dispose(); _mixedSystem = null;
        _systemResampler?.Dispose(); _systemResampler = null;
        _mixedWriter?.Dispose(); _mixedWriter = null;
        _mixer = null; _micSampleProvider = null; _systemSampleProvider = null;
        _mixCts?.Dispose(); _mixCts = null;
        _isRecording = false;
        return Task.CompletedTask;
    }
}
#endif
