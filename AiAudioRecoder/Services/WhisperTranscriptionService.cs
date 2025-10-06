using System.IO;
using System.Threading.Tasks;
using Whisper.net;

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

            try
            {
                using var factory = WhisperFactory.FromPath(_modelPath);
                using var processor = factory.CreateBuilder()
                    .WithLanguage("auto")
                    .Build();
                using var audioStream = File.OpenRead(audioFilePath);
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
        }
    }
}
