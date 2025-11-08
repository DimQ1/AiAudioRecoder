using System;
using System.IO;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;
using NAudio.Wave;

namespace AiAudioRecoder.Services
{
    public enum DeviceType
    {
        Cpu,
        Gpu,
    }

    public class WhisperTranscriptionService
    {
        public string ModelPath { get; set; }
        public DeviceType Device { get; set; }
        public int DeviceIndex { get; set; }

        public WhisperTranscriptionService(
            string modelPath,
            DeviceType device = DeviceType.Cpu,
            int deviceIndex = 0
        )
        {
            ModelPath = modelPath;
            Device = device;
            DeviceIndex = deviceIndex;
        }

        public async Task<string> TranscribeAsync(string audioFilePath)
        {
            if (!File.Exists(ModelPath))
            {
                System.Diagnostics.Debug.WriteLine($"Model not found: {ModelPath}. Skipping transcription.");
                return "Транскрипция недоступна: модель не загружена.";
            }

            string? tempPath = null;
            try
            {
                // Resample audio to 16kHz mono for Whisper compatibility
                tempPath = Path.GetTempFileName() + ".wav";
                using (var reader = new WaveFileReader(audioFilePath))
                {
                    IWaveProvider provider = reader;
                    if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1)
                    {
                        provider = new NAudio.Wave.MediaFoundationResampler(provider, new WaveFormat(16000, 1));
                    }
                    using (var output = new WaveFileWriter(tempPath, new WaveFormat(16000, 1)))
                    {
                        var buffer = new float[4096];
                        int samplesRead;
                        while ((samplesRead = provider.ToSampleProvider().Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.WriteSamples(buffer, 0, samplesRead);
                        }
                    }
                }

                using var factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions()
                {
                    UseGpu = Device == DeviceType.Gpu,
                    GpuDevice = DeviceIndex
                });
                // Multiple Runtimes Support: Whisper.net automatically selects the best runtime based on installed packages and platform
                // Priority: Cuda > Vulkan > CoreML > OpenVino > Cpu > NoAvx
                // To customize, use WhisperFactory.FromPath(path, new RuntimeOptions { RuntimeLibraryOrder = [...] })
                using var processor = factory.CreateBuilder()
                    .WithLanguage("auto")
                    .Build();
                using var audioStream = File.OpenRead(tempPath);
                var text = string.Empty;
                await foreach (var result in processor.ProcessAsync(audioStream))
                {
                    text += result.Text + " ";
                }
                return text.Trim();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Transcription error: {ex.Message}");
                return "Ошибка транскрипции.";
            }
            finally
            {
                // Cleanup temp file safely
                if (tempPath != null && File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Could not delete temp file {tempPath}: {ex.Message}");
                    }
                }
            }
        }

        public async Task<string> TranscribeAsync(string audioFilePath, System.Threading.CancellationToken cancellationToken)
        {
            if (!File.Exists(ModelPath))
            {
                System.Diagnostics.Debug.WriteLine($"Model not found: {ModelPath}. Skipping transcription.");
                return "Транскрипция недоступна: модель не загружена.";
            }

            string? tempPath = null;
            try
            {
                tempPath = Path.GetTempFileName() + ".wav";
                using (var reader = new WaveFileReader(audioFilePath))
                {
                    IWaveProvider provider = reader;
                    if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1)
                    {
                        provider = new MediaFoundationResampler(provider, new WaveFormat(16000, 1));
                    }
                    using (var output = new WaveFileWriter(tempPath, new WaveFormat(16000, 1)))
                    {
                        var buffer = new float[4096];
                        int samplesRead;
                        while ((samplesRead = provider.ToSampleProvider().Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            output.WriteSamples(buffer, 0, samplesRead);
                        }
                    }
                }

                using var factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions()
                {
                    UseGpu = Device == DeviceType.Gpu,
                    GpuDevice = DeviceIndex
                });
                using var processor = factory.CreateBuilder().WithLanguage("auto").Build();
                using var audioStream = File.OpenRead(tempPath);
                var text = string.Empty;
                await foreach (var result in processor.ProcessAsync(audioStream, cancellationToken))
                {
                    text += result.Text + " ";
                }
                return text.Trim();
            }
            catch (OperationCanceledException)
            {
                return string.Empty; // cancellation path
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Transcription error: {ex.Message}");
                return "Ошибка транскрипции.";
            }
            finally
            {
                if (tempPath != null && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }
    }
}
