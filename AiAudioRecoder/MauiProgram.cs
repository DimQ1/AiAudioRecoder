using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.IO;
using Whisper.net.Ggml;
using Microsoft.Maui.Storage;
using Microsoft.Extensions.DependencyInjection;
using AiAudioRecoder.Services;
using AiAudioRecoder.Models;
using AiAudioRecoder.Converters;

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

#if DEBUG
        builder.Logging.AddDebug();
#endif

    builder.Services.AddSingleton<HttpClient>();

        // Register core services first
        builder.Services.AddSingleton<IAudioRecorderService, AudioRecorderService>();
        
        // Database setup - create logger first
        var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        var logger = loggerFactory.CreateLogger<AudioMetadataDatabase>();
        
        var dbRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiAudioRecoder", "Databases");
        Directory.CreateDirectory(dbRoot);
        var dbPath = Path.Combine(dbRoot, "audio_metadata.db3");
        
        // Create database with logger
        var audioDb = new AudioMetadataDatabase(dbPath, logger);
        builder.Services.AddSingleton<AudioMetadataDatabase>(audioDb);
        
        // Model setup
        var docsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var modelDir = Path.Combine(docsPath, "AiAudioRecoder", "models");
        Directory.CreateDirectory(modelDir);
        var currentModel = Preferences.Default.Get("CurrentModel", "base");
        var modelPath = Path.Combine(modelDir, $"ggml-{currentModel}.bin");
        
        if (!File.Exists(modelPath))
        {
            _ = DownloadModelAsync(currentModel, modelDir);
        }
        
        var deviceStr = Preferences.Default.Get("DeviceType", "CPU");
        var device = deviceStr == "GPU" ? AiAudioRecoder.Services.DeviceType.Gpu : AiAudioRecoder.Services.DeviceType.Cpu;
        var deviceIndex = Preferences.Default.Get("DeviceNumber", 0);
        
        builder.Services.AddSingleton<WhisperTranscriptionService>(provider => 
            new WhisperTranscriptionService(modelPath, device, deviceIndex));
        
        builder.Services.AddSingleton<TranscriptionQueueService>();
        builder.Services.AddSingleton<TextAnalysisService>();
        
        var modelDbPath = Path.Combine(dbRoot, "models.db3");
        builder.Services.AddSingleton(new ModelInfoDatabase(modelDbPath));

        // Register pages
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<RecordPage>();
        builder.Services.AddTransient<TranscribePage>();
        builder.Services.AddTransient<ModelsPage>();
        
        // Register converters
        builder.Services.AddTransient<DurationConverter>();
        builder.Services.AddTransient<TranscriptionStatusConverter>();
        builder.Services.AddTransient<StatusColorConverter>();
        builder.Services.AddTransient<InverseBoolConverter>();
        builder.Services.AddTransient<FilePathConverter>();
        builder.Services.AddTransient<QueueStatusConverter>();

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
            
            System.Diagnostics.Debug.WriteLine($"Model {modelName} downloaded successfully to {path}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error downloading model {modelName}: {ex.Message}");
        }
    }
}
