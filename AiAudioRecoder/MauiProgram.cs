using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.IO;
using Whisper.net.Ggml;
using Microsoft.Maui.Storage;

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
        var dbRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiAudioRecoder", "Databases");
        Directory.CreateDirectory(dbRoot);
        var dbPath = Path.Combine(dbRoot, "audio_metadata.db3");
        builder.Services.AddSingleton(new AiAudioRecoder.Services.AudioMetadataDatabase(dbPath));
        // Путь к модели: используем текущую модель из Preferences, по умолчанию base
        var docsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var modelDir = Path.Combine(docsPath, "AiAudioRecoder", "models");
        Directory.CreateDirectory(modelDir);
        var currentModel = Preferences.Get("CurrentModel", "base");
        var modelPath = Path.Combine(modelDir, $"ggml-{currentModel}.bin");
        if (!File.Exists(modelPath))
        {
            // Автоматическая загрузка текущей модели при первом запуске
            DownloadModelAsync(currentModel, modelDir).Wait();
        }
        builder.Services.AddSingleton(
            new AiAudioRecoder.Services.WhisperTranscriptionService(modelPath)
        );
        var modelDbPath = Path.Combine(dbRoot, "models.db3");
        builder.Services.AddSingleton(new AiAudioRecoder.Models.ModelInfoDatabase(modelDbPath));

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
