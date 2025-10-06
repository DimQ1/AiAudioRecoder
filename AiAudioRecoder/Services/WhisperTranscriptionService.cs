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
                throw new FileNotFoundException($"Файл модели Whisper не найден: {_modelPath}. Пожалуйста, скачайте модель через интерфейс приложения.");

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
    }
}
