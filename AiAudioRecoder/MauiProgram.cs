using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui;

namespace AiAudioRecoder;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkitMediaElement()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

	builder.Services.AddSingleton<AiAudioRecoder.Services.IAudioRecorderService, AiAudioRecoder.Services.AudioRecorderService>();
	var dbPath = Path.Combine(FileSystem.Current.AppDataDirectory, "audio_metadata.db3");
	builder.Services.AddSingleton(new AiAudioRecoder.Services.AudioMetadataDatabase(dbPath));

#if DEBUG
	builder.Logging.AddDebug();
#endif

	return builder.Build();
	}
}
