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
        Gpu
    }

    public class WhisperTranscriptionService
    {
        private readonly string _modelPath;
        private readonly DeviceType _device;

        public WhisperTranscriptionService(string modelPath, DeviceType device = DeviceType.Cpu)
        {
            _modelPath = modelPath;
            _device = device;
        }

        public async Task<string> TranscribeAsync(string audioFilePath)
        {
            if (!File.Exists(_modelPath))
            {
                System.Diagnostics.Debug.WriteLine($"Model not found: {_modelPath}. Skipping transcription.");
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

                using var factory = WhisperFactory.FromPath(_modelPath);
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
    }
}
