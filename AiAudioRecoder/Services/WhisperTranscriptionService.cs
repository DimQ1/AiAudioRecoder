using System;
using System.IO;
using System.Threading.Tasks;
using Whisper.net;
using NAudio.Wave;

namespace AiAudioRecoder.Services
{
    public class WhisperTranscriptionService
    {
        private readonly string _modelPath;

        public WhisperTranscriptionService(string modelPath)
        {
            _modelPath = modelPath;
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
                    using (var resampler = new WaveFormatConversionStream(new WaveFormat(16000, 1), reader))
                    {
                        using (var output = new WaveFileWriter(tempPath, new WaveFormat(16000, 1)))
                        {
                            var buffer = new byte[4096];
                            int bytesRead;
                            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                output.Write(buffer, 0, bytesRead);
                            }
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
