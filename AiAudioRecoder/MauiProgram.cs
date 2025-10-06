using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.IO;
using Whisper.net.Ggml;

namespace AiAudioRecoder;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<
            AiAudioRecoder.Services.IAudioRecorderService,
            AiAudioRecoder.Services.AudioRecorderService
        >();
        var dbPath = Path.Combine(FileSystem.Current.AppDataDirectory, "audio_metadata.db3");
        builder.Services.AddSingleton(new AiAudioRecoder.Services.AudioMetadataDatabase(dbPath));
        // Путь к модели: по умолчанию ищем скачанную ggml-base.bin в MyDocuments, fallback на tiny если нет
        var docsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var modelDir = Path.Combine(docsPath, "AiAudioRecoder", "models");
        Directory.CreateDirectory(modelDir);
        var baseModelPath = Path.Combine(modelDir, "ggml-base.bin");
        var modelPath = baseModelPath;
        if (!File.Exists(baseModelPath))
        {
            // Автоматическая загрузка base модели при первом запуске
            DownloadModelAsync("base", modelDir).Wait();
            modelPath = baseModelPath;
        }
        builder.Services.AddSingleton(
            new AiAudioRecoder.Services.WhisperTranscriptionService(modelPath)
        );

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    private static async Task DownloadModelAsync(string modelName, string modelDir)
    {
        try
        {
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{modelName}.bin";
            var fileName = $"ggml-{modelName}.bin";
            var path = Path.Combine(modelDir, fileName);
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(path);
            await stream.CopyToAsync(fileStream);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error downloading model: {ex.Message}");
        }
    }
}
