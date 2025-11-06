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
    private WaveInEvent? _waveIn;
    private WasapiLoopbackCapture? _loopback;
    private WaveFileWriter? _writer;

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

    public Task<string?> StartRecordingAsync(string? folderName = null, string? fileName = null, string source = "mic")
    {
        if (_isRecording) return Task.FromResult<string?>(null);
        var dateFolder = string.IsNullOrEmpty(folderName) ? DateTime.Now.ToString("yyyy-MM-dd") : folderName;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dir = Path.Combine(root, "AiAudioRecoder", "audioData", dateFolder);
        Directory.CreateDirectory(dir);
        var name = string.IsNullOrEmpty(fileName) ? $"audio_{DateTime.Now:yyyyMMdd_HHmmss}" : fileName;
        if (source == "system") name += "_system";
        name += ".wav";
        _filePath = Path.Combine(dir, name);

        if (source == "system")
        {
            try
            {
                _loopback = new WasapiLoopbackCapture();
                _loopback.DataAvailable += (s, e) => _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                _writer = new WaveFileWriter(_filePath, _loopback.WaveFormat);
                _loopback.StartRecording();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Loopback start error: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }
        else
        {
            try
            {
                _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(44100, 1) };
                _waveIn.DataAvailable += (s, e) => _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                _writer = new WaveFileWriter(_filePath, _waveIn.WaveFormat);
                _waveIn.StartRecording();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Mic start error: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }
        _isRecording = true;
        return Task.FromResult<string?>(_filePath);
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
        try
        {
            var mixerFloat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var outputFormat = new WaveFormat(44100, 16, 2);

            // Mic
            _mixedMic = new WaveInEvent { WaveFormat = new WaveFormat(44100, 1) };
            var micBuffer = new BufferedWaveProvider(_mixedMic.WaveFormat) { DiscardOnBufferOverflow = true };
            _mixedMic.DataAvailable += (s, e) => micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            ISampleProvider micSample = new MonoToStereoSampleProvider(micBuffer.ToSampleProvider());
            _micSampleProvider = micSample;

            // System
            _mixedSystem = new WasapiLoopbackCapture();
            var sysFormat = _mixedSystem.WaveFormat;
            var sysBuffer = new BufferedWaveProvider(sysFormat) { DiscardOnBufferOverflow = true };
            _mixedSystem.DataAvailable += (s, e) => sysBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            ISampleProvider sysSample = sysBuffer.ToSampleProvider();
            if (sysFormat.SampleRate != 44100 || sysFormat.Channels != 2 || sysFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            {
                _systemResampler = new MediaFoundationResampler(sysBuffer, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ResamplerQuality = 60 };
                sysSample = _systemResampler.ToSampleProvider();
            }
            _systemSampleProvider = sysSample;

            _mixer = new MixingSampleProvider(mixerFloat) { ReadFully = true };
            _mixer.AddMixerInput(_micSampleProvider);
            _mixer.AddMixerInput(_systemSampleProvider);

            _mixedWriter = new WaveFileWriter(mixedPath, outputFormat);
            _mixCts = new CancellationTokenSource();
            _mixedMic.StartRecording();
            _mixedSystem.StartRecording();

            _mixTask = Task.Run(async () =>
            {
                var floatBuffer = new float[mixerFloat.SampleRate / 10 * mixerFloat.Channels];
                var pcmBuffer = new byte[floatBuffer.Length * 2];
                try
                {
                    while (!_mixCts!.IsCancellationRequested)
                    {
                        int read = _mixer!.Read(floatBuffer, 0, floatBuffer.Length);
                        if (read > 0)
                        {
                            int idx = 0;
                            for (int i = 0; i < read; i++)
                            {
                                var sample = floatBuffer[i];
                                if (sample > 1f) sample = 1f; else if (sample < -1f) sample = -1f;
                                short pcm = (short)(sample * short.MaxValue);
                                pcmBuffer[idx++] = (byte)(pcm & 0xFF);
                                pcmBuffer[idx++] = (byte)((pcm >> 8) & 0xFF);
                            }
                            _mixedWriter!.Write(pcmBuffer, 0, idx);
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
        // stop mixed
        if (_mixCts != null)
        {
            try { _mixCts.Cancel(); } catch { }
            try { _mixTask?.Wait(2000); } catch { }
        }
        _mixedMic?.StopRecording(); _mixedMic?.Dispose(); _mixedMic = null;
        _mixedSystem?.StopRecording(); _mixedSystem?.Dispose(); _mixedSystem = null;
        _systemResampler?.Dispose(); _systemResampler = null;
        _mixedWriter?.Dispose(); _mixedWriter = null;
        _mixer = null; _micSampleProvider = null; _systemSampleProvider = null;
        _mixCts?.Dispose(); _mixCts = null;

        // single
        _loopback?.StopRecording(); _loopback?.Dispose(); _loopback = null;
        _waveIn?.StopRecording(); _waveIn?.Dispose(); _waveIn = null;
        _writer?.Dispose(); _writer = null;

        _isRecording = false;
        return Task.CompletedTask;
    }
}
#endif
