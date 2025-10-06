using Microsoft.Extensions.Logging;

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
        var baseModelPath = Path.Combine(modelDir, "ggml-base.bin");
        var modelPath = File.Exists(baseModelPath) ? baseModelPath : "whisper-tiny.bin"; // Fallback
        builder.Services.AddSingleton(
            new AiAudioRecoder.Services.WhisperTranscriptionService(modelPath)
        );

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
